// iss14: Economy
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Administration.Logs;
using Content.Server.Database;
using Content.Shared.CCVar;
using Content.Shared.Database;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server.Economy;

/// <inheritdoc cref="IBankManager"/>
public sealed partial class BankManager : IBankManager, IPostInjectInit
{
    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private UserDbDataManager _userDb = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IAdminLogManager _adminLog = default!;
    [Dependency] private IEntityManager _entMan = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private ILogManager _logManager = default!;

    private ISawmill _sawmill = default!;

    /// <summary>Cached balances of connected (loaded) players.</summary>
    private readonly Dictionary<NetUserId, long> _balances = new();

    /// <summary>
    /// Per-user write queue: the tail task of each user's chain. Writes for one user run strictly in order, so a
    /// fast credit+debit can never race in the database. Writes for different users run concurrently.
    /// </summary>
    private readonly Dictionary<NetUserId, Task> _writeChains = new();

    public event Action<BankBalanceChangedEvent>? BalanceChanged;

    void IPostInjectInit.PostInject()
    {
        _sawmill = _logManager.GetSawmill("bank");
        _userDb.AddOnLoadPlayer(LoadData);
        _userDb.AddOnPlayerDisconnect(ClientDisconnected);
    }

    private async Task LoadData(ICommonSession session, CancellationToken cancel)
    {
        var balance = await _db.GetBankBalanceAsync(session.UserId, cancel);
        cancel.ThrowIfCancellationRequested();
        _balances[session.UserId] = balance;
    }

    private void ClientDisconnected(ICommonSession session)
    {
        _balances.Remove(session.UserId);
    }

    public bool IsLoaded(NetUserId userId) => _balances.ContainsKey(userId);

    public long GetBalance(NetUserId userId) => _balances.GetValueOrDefault(userId);

    private long MaxBalance => Math.Max(0, _cfg.GetCVar(CCVars.EconomyMaxBalance));

    public bool TryDebit(NetUserId userId, long amount, string reason, out long newBalance)
    {
        newBalance = 0;
        if (amount <= 0 || !_balances.TryGetValue(userId, out var current) || current < amount)
            return false;

        newBalance = current - amount;
        Apply(userId, current, newBalance, reason, salary: false);
        return true;
    }

    public bool Credit(NetUserId userId, long amount, string reason, out long newBalance, bool salary = false)
    {
        newBalance = 0;
        if (amount <= 0 || !_balances.TryGetValue(userId, out var current))
            return false;

        var max = MaxBalance;
        newBalance = max > 0 ? Math.Min(max, current + amount) : current + amount;
        Apply(userId, current, newBalance, reason, salary);
        return true;
    }

    public bool TryTransfer(NetUserId from, NetUserId to, long amount, string reason)
    {
        if (amount <= 0 || from == to)
            return false;

        if (!_balances.TryGetValue(from, out var fromBalance) || !_balances.TryGetValue(to, out var toBalance))
            return false;

        if (fromBalance < amount)
            return false;

        var max = MaxBalance;
        if (max > 0 && toBalance + amount > max)
            return false;

        Apply(from, fromBalance, fromBalance - amount, $"transfer to {to}: {reason}", salary: false);
        Apply(to, toBalance, toBalance + amount, $"transfer from {from}: {reason}", salary: false);
        return true;
    }

    /// <summary>Commits a cached balance change: cache, DB write-through, admin log, events.</summary>
    private void Apply(NetUserId userId, long oldBalance, long newBalance, string reason, bool salary)
    {
        _balances[userId] = newBalance;

        var delta = newBalance - oldBalance;
        Enqueue(userId, async () =>
        {
            // The cache already has the authoritative value; delta-adjust the row so an admin edit made directly
            // in the database between two writes is not clobbered by a stale absolute value.
            var result = await _db.AdjustBankBalanceAsync(userId, delta, allowNegative: true, maxBalance: 0, salary);
            if (result == null)
                await _db.SetBankBalanceAsync(userId, newBalance);
        });

        Log(userId, oldBalance, newBalance, reason);
    }

    private void Log(NetUserId userId, long oldBalance, long newBalance, string reason)
    {
        var name = _players.TryGetSessionById(userId, out var session) ? session.Name : userId.ToString();
        var delta = newBalance - oldBalance;
        var sign = delta >= 0 ? "+" : string.Empty;

        _adminLog.Add(LogType.Economy, LogImpact.Low,
            $"Bank: {name} ({userId}) {sign}{delta} credits ({oldBalance} -> {newBalance}): {reason}");

        var ev = new BankBalanceChangedEvent(userId, oldBalance, newBalance, reason);
        BalanceChanged?.Invoke(ev);
        _entMan.EventBus.RaiseEvent(EventSource.Local, ev);
    }

    private void Enqueue(NetUserId userId, Func<Task> write)
    {
        var previous = _writeChains.GetValueOrDefault(userId) ?? Task.CompletedTask;
        var next = previous.ContinueWith(async _ =>
            {
                try
                {
                    await write();
                }
                catch (Exception e)
                {
                    _sawmill.Error($"Bank write for {userId} failed: {e}");
                }
            },
            TaskScheduler.Default).Unwrap();

        _writeChains[userId] = next;
    }

    public async Task FlushAsync()
    {
        await Task.WhenAll(_writeChains.Values.ToArray());
    }

    #region Offline / direct database access

    public async Task<long> GetBalanceAsync(NetUserId userId)
    {
        if (_balances.TryGetValue(userId, out var cached))
            return cached;

        return await _db.GetBankBalanceAsync(userId);
    }

    public async Task SetBalanceAsync(NetUserId userId, long balance, string reason)
    {
        balance = Math.Max(0, balance);
        var max = MaxBalance;
        if (max > 0)
            balance = Math.Min(max, balance);

        if (_balances.TryGetValue(userId, out var current))
        {
            Apply(userId, current, balance, reason, salary: false);
            await FlushUser(userId);
            return;
        }

        var old = await _db.GetBankBalanceAsync(userId);
        await _db.SetBankBalanceAsync(userId, balance);
        Log(userId, old, balance, reason);
    }

    public async Task<long?> AdjustBalanceAsync(NetUserId userId, long delta, string reason)
    {
        if (_balances.TryGetValue(userId, out var current))
        {
            var next = current + delta;
            if (next < 0)
                return null;

            var max = MaxBalance;
            if (max > 0)
                next = Math.Min(max, next);

            Apply(userId, current, next, reason, salary: false);
            await FlushUser(userId);
            return next;
        }

        var old = await _db.GetBankBalanceAsync(userId);
        var result = await _db.AdjustBankBalanceAsync(userId, delta, allowNegative: false, MaxBalance);
        if (result != null)
            Log(userId, old, result.Value, reason);

        return result;
    }

    private Task FlushUser(NetUserId userId)
        => _writeChains.GetValueOrDefault(userId) ?? Task.CompletedTask;

    #endregion
}
