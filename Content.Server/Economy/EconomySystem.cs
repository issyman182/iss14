// iss14: Economy
using System.Linq;
using Content.Server.Administration.Logs;
using Content.Server.Cargo.Systems;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Shared.Access.Systems;
using Content.Shared.Cargo.Components;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.CCVar;
using Content.Shared.Database;
using Content.Shared.Economy;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.Server.Economy;

/// <summary>
/// The one place money moves. Every feature (ATM, vending, cash register, cargo, fines, tasks, heists) books
/// through <see cref="TryTransfer"/> between two <see cref="EconomyParty"/> values, so the rules — who may spend a
/// department account, fingerprint checks for ID card payments, balance caps, the transaction log — live here once.
/// </summary>
public sealed partial class EconomySystem : EntitySystem
{
    [Dependency] private IBankManager _bank = default!;
    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private IAdminLogManager _adminLog = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private CargoSystem _cargo = default!;
    [Dependency] private AccessReaderSystem _access = default!;
    [Dependency] private BankCardSystem _bankCard = default!;
    [Dependency] private ServerGameTicker _ticker = default!;

    private EntityQuery<StationBankAccountComponent> _stationBankQuery;

    public override void Initialize()
    {
        base.Initialize();
        _stationBankQuery = GetEntityQuery<StationBankAccountComponent>();
    }

    #region Balances

    /// <summary>Balance of a party; players must be connected (cached), otherwise 0.</summary>
    public long GetBalance(EconomyParty party)
    {
        switch (party.Kind)
        {
            case EconomyPartyKind.Player:
                return party.UserId is { } user ? _bank.GetBalance(user) : 0;
            case EconomyPartyKind.Department:
                if (party.Station is not { } station || party.Account is not { } account)
                    return 0;
                return _cargo.GetBalanceFromAccount(station, account);
            case EconomyPartyKind.Station:
                if (party.Station is not { } st || !_stationBankQuery.TryComp(st, out var bank))
                    return 0;
                return _cargo.GetBalanceFromAccount((st, bank), bank.PrimaryAccount);
            default:
                return long.MaxValue;
        }
    }

    /// <summary>Whether this party can currently be charged at all (connected player / existing station account).</summary>
    public bool Exists(EconomyParty party)
    {
        switch (party.Kind)
        {
            case EconomyPartyKind.Player:
                return party.UserId is { } user && _bank.IsLoaded(user);
            case EconomyPartyKind.Department:
                return party.Station is { } station && party.Account is { } account
                       && _cargo.TryGetAccount(station, account, out _);
            case EconomyPartyKind.Station:
                return party.Station is { } st && _stationBankQuery.HasComp(st);
            default:
                return true;
        }
    }

    #endregion

    #region Transfers

    /// <summary>Takes money from <paramref name="payer"/> out of the economy (vending, fees, purchases from CentComm).</summary>
    public bool TryCharge(EconomyParty payer, long amount, string reason, out EconomyPaymentError error)
        => TryTransfer(payer, EconomyParty.External, amount, reason, out error);

    /// <summary>
    /// Pays <paramref name="payee"/> new money (salary, CentComm contract, heist). Players need not be connected: an
    /// offline player's row is adjusted directly in the database.
    /// </summary>
    public bool Pay(EconomyParty payee, long amount, string reason)
    {
        if (amount <= 0)
            return false;

        if (payee.Kind == EconomyPartyKind.Player && payee.UserId is { } user && !_bank.IsLoaded(user))
        {
            _ = _bank.AdjustBalanceAsync(user, amount, reason);
            LogTransaction(EconomyParty.External, payee, amount, reason);
            return true;
        }

        return TryTransfer(EconomyParty.External, payee, amount, reason, out _);
    }

    /// <summary>
    /// Moves <paramref name="amount"/> from <paramref name="from"/> to <paramref name="to"/>. Either side may be
    /// <see cref="EconomyParty.External"/>. Department/station accounts are int-sized; player accounts respect the
    /// max balance cap (the surplus is discarded, as at the ATM). Atomic: nothing moves if the debit fails.
    /// </summary>
    public bool TryTransfer(EconomyParty from, EconomyParty to, long amount, string reason, out EconomyPaymentError error)
    {
        error = EconomyPaymentError.None;

        if (amount <= 0)
        {
            error = EconomyPaymentError.InvalidAmount;
            return false;
        }

        if (!Exists(from) || !Exists(to))
        {
            error = EconomyPaymentError.NoAccount;
            return false;
        }

        if (from.Kind != EconomyPartyKind.External && GetBalance(from) < amount)
        {
            error = EconomyPaymentError.InsufficientFunds;
            return false;
        }

        if (to.Kind is EconomyPartyKind.Department or EconomyPartyKind.Station && GetBalance(to) + amount > int.MaxValue)
        {
            error = EconomyPaymentError.AccountFull;
            return false;
        }

        if (from.Kind is EconomyPartyKind.Department or EconomyPartyKind.Station && amount > int.MaxValue)
        {
            error = EconomyPaymentError.InsufficientFunds;
            return false;
        }

        // Debit first; a failed debit leaves everything untouched.
        if (!Debit(from, amount, $"{reason} -> {to}"))
        {
            error = EconomyPaymentError.InsufficientFunds;
            return false;
        }

        Credit(to, amount, $"{reason} <- {from}");
        LogTransaction(from, to, amount, reason);
        return true;
    }

    private bool Debit(EconomyParty party, long amount, string reason)
    {
        switch (party.Kind)
        {
            case EconomyPartyKind.External:
                return true;
            case EconomyPartyKind.Player:
                return party.UserId is { } user && _bank.TryDebit(user, amount, reason, out _);
            case EconomyPartyKind.Department:
                return _cargo.TryAdjustBankAccount(party.Station!.Value, party.Account!.Value, -(int) amount);
            case EconomyPartyKind.Station:
            {
                var station = party.Station!.Value;
                var bank = _stationBankQuery.Comp(station);
                return _cargo.TryAdjustBankAccount((station, bank), bank.PrimaryAccount, -(int) amount);
            }
            default:
                return false;
        }
    }

    private void Credit(EconomyParty party, long amount, string reason)
    {
        switch (party.Kind)
        {
            case EconomyPartyKind.Player:
                if (party.UserId is { } user)
                    _bank.Credit(user, amount, reason, out _);
                break;
            case EconomyPartyKind.Department:
                _cargo.TryAdjustBankAccount(party.Station!.Value, party.Account!.Value, (int) amount);
                break;
            case EconomyPartyKind.Station:
            {
                var station = party.Station!.Value;
                var bank = _stationBankQuery.Comp(station);
                _cargo.TryAdjustBankAccount((station, bank), bank.PrimaryAccount, (int) amount);
                break;
            }
        }
    }

    /// <summary>
    /// Removes up to <paramref name="amount"/> from a station, spread over all its accounts in proportion to their
    /// balances (a heist hurts everyone). Returns what was actually removed.
    /// </summary>
    public long DrainStation(EntityUid station, long amount, string reason)
    {
        if (amount <= 0 || !_stationBankQuery.TryComp(station, out var bank))
            return 0;

        var snapshot = _cargo.GetAccounts((station, bank)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var total = snapshot.Values.Sum(v => (long) Math.Max(0, v));
        if (total <= 0)
            return 0;

        var take = Math.Min(amount, total);
        var removed = 0L;
        var accounts = snapshot.Keys.ToList();
        for (var i = 0; i < accounts.Count; i++)
        {
            var account = accounts[i];
            var balance = Math.Max(0, snapshot[account]);
            // Last account gets the rounding remainder so the sum is exact.
            var share = i == accounts.Count - 1
                ? take - removed
                : (long) Math.Floor(take * (balance / (double) total));
            share = Math.Clamp(share, 0, balance);
            if (share == 0)
                continue;

            _cargo.TryAdjustBankAccount((station, bank), account, -(int) share, dirty: false);
            removed += share;
        }

        Dirty(station, bank);
        LogTransaction(EconomyParty.StationBudget(station), EconomyParty.External, removed, reason);
        return removed;
    }

    #endregion

    #region Access & card resolution

    /// <summary>
    /// Resolves the player account behind an ID card for a payment made by <paramref name="user"/>. Honors
    /// <c>economy.atm_require_fingerprint</c>: when set, the card's recorded fingerprint must match the user's.
    /// </summary>
    public bool TryGetCardPayer(EntityUid card, EntityUid? user, out EconomyParty payer, out EconomyPaymentError error)
    {
        payer = default;
        error = EconomyPaymentError.None;

        if (!TryComp<BankCardComponent>(card, out var bankCard) || bankCard.OwnerUserId is not { } owner)
        {
            error = EconomyPaymentError.NoAccount;
            return false;
        }

        if (user != null && _cfg.GetCVar(CCVars.EconomyAtmRequireFingerprint)
            && _bankCard.VerifyHolder(card, user.Value) == AtmVerifyResult.Mismatch)
        {
            error = EconomyPaymentError.FingerprintMismatch;
            return false;
        }

        if (!_bank.IsLoaded(owner))
        {
            error = EconomyPaymentError.NoAccount;
            return false;
        }

        payer = EconomyParty.Player(owner);
        return true;
    }

    /// <summary>Whether <paramref name="user"/> may spend the given department account (head access, or Captain).</summary>
    public bool CanSpendDepartment(EntityUid user, ProtoId<CargoAccountPrototype> account)
    {
        var tags = _access.FindAccessTags(user);
        if (tags.Contains("Captain"))
            return true;

        if (!ProtoMan.TryIndex<EconomyDepartmentPrototype>(account.Id, out var department))
            return false;

        return department.HeadAccess.Any(tags.Contains);
    }

    /// <summary>Whether <paramref name="user"/> belongs to the department (member or head access, or Captain).</summary>
    public bool IsDepartmentMember(EntityUid user, ProtoId<CargoAccountPrototype> account)
    {
        var tags = _access.FindAccessTags(user);
        if (tags.Contains("Captain"))
            return true;

        if (!ProtoMan.TryIndex<EconomyDepartmentPrototype>(account.Id, out var department))
            return false;

        return department.HeadAccess.Any(tags.Contains) || department.MemberAccess.Any(tags.Contains);
    }

    /// <summary>All department accounts <paramref name="user"/> may spend on <paramref name="station"/>.</summary>
    public List<ProtoId<CargoAccountPrototype>> GetSpendableDepartments(EntityUid user, EntityUid station)
    {
        var result = new List<ProtoId<CargoAccountPrototype>>();
        if (!_stationBankQuery.HasComp(station))
            return result;

        foreach (var account in _cargo.GetAccounts(station).Keys)
        {
            if (CanSpendDepartment(user, account))
                result.Add(account);
        }

        return result;
    }

    #endregion

    #region Logging

    /// <summary>
    /// Writes a booking that was executed outside <see cref="TryTransfer"/> (the ATM's offline path, cash handling)
    /// to the admin log and the transaction table.
    /// </summary>
    public void Record(EconomyParty from, EconomyParty to, long amount, string reason) => LogTransaction(from, to, amount, reason);

    private void LogTransaction(EconomyParty from, EconomyParty to, long amount, string reason)
    {
        _adminLog.Add(LogType.Economy, LogImpact.Low, $"Economy: {amount} credits {from} -> {to}: {reason}");

        var row = new BankTransaction
        {
            Time = DateTime.UtcNow,
            RoundId = _ticker.RoundId == 0 ? null : _ticker.RoundId,
            FromKind = from.LogKind,
            FromId = from.LogId,
            ToKind = to.LogKind,
            ToId = to.LogId,
            Amount = amount,
            Reason = reason.Length > 512 ? reason[..512] : reason,
        };

        _bank.EnqueueWrite(() => _db.AddBankTransactionAsync(row));
    }

    /// <summary>Human-readable name of a party for popups and logs.</summary>
    public string Describe(EconomyParty party)
    {
        switch (party.Kind)
        {
            case EconomyPartyKind.Player:
                if (party.UserId is { } user && _players.TryGetSessionById(user, out var session))
                    return session.Name;
                return Loc.GetString("economy-party-player-unknown");
            case EconomyPartyKind.Department:
                if (party.Account is { } account && ProtoMan.TryIndex<CargoAccountPrototype>(account, out var proto))
                    return Loc.GetString(proto.Name);
                return party.Account?.Id ?? string.Empty;
            case EconomyPartyKind.Station:
                return Loc.GetString("economy-party-station");
            default:
                return Loc.GetString("economy-party-external");
        }
    }

    /// <summary>Localized text for a payment failure.</summary>
    public string ErrorText(EconomyPaymentError error) => error switch
    {
        EconomyPaymentError.InvalidAmount => Loc.GetString("economy-error-invalid-amount"),
        EconomyPaymentError.NoAccount => Loc.GetString("economy-error-no-account"),
        EconomyPaymentError.InsufficientFunds => Loc.GetString("economy-error-insufficient-funds"),
        EconomyPaymentError.AccountFull => Loc.GetString("economy-error-account-full"),
        EconomyPaymentError.NoAccess => Loc.GetString("economy-error-no-access"),
        EconomyPaymentError.FingerprintMismatch => Loc.GetString("economy-error-fingerprint"),
        _ => string.Empty,
    };

    #endregion
}
