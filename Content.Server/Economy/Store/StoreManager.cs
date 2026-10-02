// iss14: Economy
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Administration.Logs;
using Content.Server.Database;
using Content.Shared.CCVar;
using Content.Shared.Database;
using Content.Shared.Economy;
using Content.Shared.Economy.Store;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server.Economy.Store;

/// <summary>
/// Persistent store: titles and loadout unlocks bought with the bank balance. Unlocks and the worn title are
/// loaded with the player's data and cached; purchases go through <see cref="EconomySystem"/>.
/// </summary>
public sealed partial class StoreManager : ISharedStoreManager, IPostInjectInit
{
    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private UserDbDataManager _userDb = default!;
    [Dependency] private IServerNetManager _net = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private IEntityManager _entMan = default!;
    [Dependency] private IBankManager _bank = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IAdminLogManager _adminLog = default!;
    [Dependency] private ILogManager _logManager = default!;

    private ISawmill _sawmill = default!;

    private readonly Dictionary<NetUserId, PlayerStore> _data = new();

    public void Initialize()
    {
        _net.RegisterNetMessage<MsgStoreState>();
        _net.RegisterNetMessage<MsgStoreBuy>(OnBuy);
        _net.RegisterNetMessage<MsgStoreSetTitle>(OnSetTitle);
        _net.RegisterNetMessage<MsgStoreRequest>(msg => Send(msg.MsgChannel.UserId));
    }

    void IPostInjectInit.PostInject()
    {
        _sawmill = _logManager.GetSawmill("store");
        _userDb.AddOnLoadPlayer(LoadData);
        _userDb.AddOnFinishLoad(FinishLoad);
        _userDb.AddOnPlayerDisconnect(session => _data.Remove(session.UserId));
        _bank.BalanceChanged += ev => Send(ev.UserId);
    }

    private async Task LoadData(ICommonSession session, CancellationToken cancel)
    {
        var unlocks = await _db.GetPlayerUnlocksAsync(session.UserId, cancel);
        var title = await _db.GetPlayerTitleAsync(session.UserId, cancel);
        cancel.ThrowIfCancellationRequested();
        _data[session.UserId] = new PlayerStore(unlocks, title);
    }

    private void FinishLoad(ICommonSession session)
    {
        Send(session.UserId);
    }

    #region Queries

    public bool HasUnlock(ICommonSession session, string item)
        => _data.TryGetValue(session.UserId, out var data) && data.Owned.Contains(item);

    public bool HasUnlock(NetUserId userId, string item)
        => _data.TryGetValue(userId, out var data) && data.Owned.Contains(item);

    public IReadOnlySet<string> GetUnlocks(NetUserId userId)
        => _data.TryGetValue(userId, out var data) ? data.Owned : new HashSet<string>();

    /// <summary>The title text the player wears, or null.</summary>
    public string? GetTitleText(NetUserId userId)
    {
        if (!_data.TryGetValue(userId, out var data) || data.Title == null)
            return null;

        if (!_proto.TryIndex<StoreItemPrototype>(data.Title, out var proto) || !data.Owned.Contains(data.Title))
            return null;

        return proto.TitleText ?? Loc.GetString(proto.Name);
    }

    #endregion

    #region Mutations

    /// <summary>Grants an unlock without payment (admin). Returns false if already owned.</summary>
    public async Task<bool> GrantAsync(NetUserId userId, string item)
    {
        await _bank.FlushAsync();
        var added = await _db.AddPlayerUnlockAsync(userId, item, 0);
        if (added && _data.TryGetValue(userId, out var data))
        {
            data.Owned.Add(item);
            Send(userId);
        }

        return added;
    }

    /// <summary>Revokes an unlock (admin). Returns false if not owned.</summary>
    public async Task<bool> RevokeAsync(NetUserId userId, string item)
    {
        await _bank.FlushAsync();
        var removed = await _db.RemovePlayerUnlockAsync(userId, item);
        if (_data.TryGetValue(userId, out var data))
        {
            data.Owned.Remove(item);
            if (data.Title == item)
            {
                data.Title = null;
                await _db.SetPlayerTitleAsync(userId, null);
            }

            Send(userId);
        }

        return removed;
    }

    private void OnBuy(MsgStoreBuy msg)
    {
        var userId = msg.MsgChannel.UserId;
        if (!_data.TryGetValue(userId, out var data))
            return;

        if (!_cfg.GetCVar(CCVars.EconomyStoreEnabled))
        {
            Send(userId, Loc.GetString("store-disabled"));
            return;
        }

        if (!_proto.TryIndex<StoreItemPrototype>(msg.Item, out var item) || item.Hidden)
        {
            Send(userId, Loc.GetString("store-unknown-item"));
            return;
        }

        if (data.Owned.Contains(item.ID))
        {
            Send(userId, Loc.GetString("store-already-owned"));
            return;
        }

        var economy = _entMan.System<EconomySystem>();
        if (!economy.TryCharge(EconomyParty.Player(userId), item.Price, $"store: {item.ID}", out var error))
        {
            Send(userId, economy.ErrorText(error));
            return;
        }

        data.Owned.Add(item.ID);
        _bank.EnqueueWrite(() => _db.AddPlayerUnlockAsync(userId, item.ID, item.Price));

        var name = _players.TryGetSessionById(userId, out var session) ? session.Name : userId.ToString();
        _adminLog.Add(LogType.Economy, LogImpact.Low, $"Store: {name} ({userId}) bought {item.ID} for {item.Price} credits");

        // Wear a bought title right away if none is worn.
        if (item.Kind == StoreItemKind.Title && data.Title == null)
        {
            data.Title = item.ID;
            _bank.EnqueueWrite(() => _db.SetPlayerTitleAsync(userId, item.ID));
            _entMan.System<CharacterTitleSystem>().Refresh(userId);
        }

        Send(userId, Loc.GetString("store-bought", ("item", Loc.GetString(item.Name))));
    }

    private void OnSetTitle(MsgStoreSetTitle msg)
    {
        var userId = msg.MsgChannel.UserId;
        if (!_data.TryGetValue(userId, out var data))
            return;

        string? title = null;
        if (!string.IsNullOrEmpty(msg.Title))
        {
            if (!data.Owned.Contains(msg.Title) || !_proto.TryIndex<StoreItemPrototype>(msg.Title, out var proto) || proto.Kind != StoreItemKind.Title)
            {
                Send(userId, Loc.GetString("store-title-not-owned"));
                return;
            }

            title = msg.Title;
        }

        data.Title = title;
        _entMan.System<CharacterTitleSystem>().Refresh(userId);
        _bank.EnqueueWrite(() => _db.SetPlayerTitleAsync(userId, title));

        Send(userId, title == null ? Loc.GetString("store-title-cleared") : Loc.GetString("store-title-set"));
    }

    #endregion

    public void Send(NetUserId userId, string status = "")
    {
        if (!_data.TryGetValue(userId, out var data) || !_players.TryGetSessionById(userId, out var session))
            return;

        var msg = new MsgStoreState
        {
            Balance = _bank.GetBalance(userId),
            Owned = new HashSet<string>(data.Owned),
            Title = data.Title,
            Status = status,
        };
        _net.ServerSendMessage(msg, session.Channel);
    }

    private sealed class PlayerStore(HashSet<string> owned, string? title)
    {
        public readonly HashSet<string> Owned = owned;
        public string? Title = title;
    }
}
