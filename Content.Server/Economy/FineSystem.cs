// iss14: Economy
using System.Linq;
using Content.Server.Administration.Logs;
using Content.Server.CriminalRecords.Systems;
using Content.Server.GameTicking;
using Content.Server.Station.Systems;
using Content.Shared.Access.Systems;
using Content.Shared.CCVar;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.CriminalRecords;
using Content.Shared.Database;
using Content.Shared.Economy;
using Content.Shared.GameTicking;
using Content.Shared.Security;
using Content.Shared.StationRecords;
using Content.Shared.StationRecords.Components;
using Robust.Shared.Containers;
using Content.Shared.StationRecords.Systems;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Network;

namespace Content.Server.Economy;

/// <summary>
/// Fines on criminal records and the legal terminal that issues, pays and settles them
/// (<see cref="FineTerminalComponent"/>). Paid fines go to the Security department account. Unpaid fines are
/// deducted from the debtor's salary at round end (<c>economy.fines_from_salary</c>). A lawyer who filed a signed
/// <see cref="LegalMandateComponent"/> can pay a client's fines from the client's account for a fee.
/// </summary>
public sealed partial class FineSystem : EntitySystem
{
    [Dependency] private EconomySystem _economy = default!;
    [Dependency] private BankCardSystem _bankCard = default!;
    [Dependency] private CriminalRecordsSystem _criminal = default!;
    [Dependency] private StationRecordsSystem _records = default!;
    [Dependency] private ServerStationSystem _station = default!;
    [Dependency] private AccessReaderSystem _access = default!;
    [Dependency] private ItemSlotsSystem _slots = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IAdminLogManager _adminLog = default!;
    [Dependency] private ServerGameTicker _ticker = default!;

    public const string SecurityAccount = "Security";

    private uint _nextFineId = 1;

    /// <summary>Filed mandates this round: client -> lawyer.</summary>
    private readonly Dictionary<NetUserId, Mandate> _mandates = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<FineTerminalComponent, BeforeActivatableUIOpenEvent>((e, c, a) => UpdateUi((e, c), a.User));
        SubscribeLocalEvent<FineTerminalComponent, EntInsertedIntoContainerMessage>((e, c, _) => UpdateUi((e, c), null));
        SubscribeLocalEvent<FineTerminalComponent, EntRemovedFromContainerMessage>((e, c, _) => UpdateUi((e, c), null));
        SubscribeLocalEvent<FineTerminalComponent, FineTerminalSelectRecordMessage>(OnSelectRecord);
        SubscribeLocalEvent<FineTerminalComponent, FineTerminalIssueMessage>(OnIssue);
        SubscribeLocalEvent<FineTerminalComponent, FineTerminalWaiveMessage>(OnWaive);
        SubscribeLocalEvent<FineTerminalComponent, FineTerminalPayOwnMessage>(OnPayOwn);
        SubscribeLocalEvent<FineTerminalComponent, FineTerminalPayClientMessage>(OnPayClient);
        SubscribeLocalEvent<FineTerminalComponent, FineTerminalFileMandateMessage>(OnFileMandate);

        SubscribeLocalEvent<SalaryDeductionEvent>(OnSalaryDeduction);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ =>
        {
            _mandates.Clear();
            _nextFineId = 1;
        });
    }

    #region Lookup helpers

    private bool TryGetCard(Entity<FineTerminalComponent> ent, out Entity<BankCardComponent> card)
    {
        card = default;
        if (!_slots.TryGetSlot(ent.Owner, FineTerminalComponent.IdSlotId, out var slot) || slot.Item is not { } item)
            return false;

        if (!TryComp<BankCardComponent>(item, out var bank))
            return false;

        card = (item, bank);
        return true;
    }

    private Entity<LegalMandateComponent>? GetMandatePaper(Entity<FineTerminalComponent> ent)
    {
        if (!_slots.TryGetSlot(ent.Owner, FineTerminalComponent.MandateSlotId, out var slot) || slot.Item is not { } item)
            return null;

        return TryComp<LegalMandateComponent>(item, out var mandate) ? (item, mandate) : null;
    }

    /// <summary>Finds the station record of a card holder by fingerprint, else by name.</summary>
    public bool TryFindRecord(EntityUid station, string? fingerprint, string? name, out StationRecordKey key)
    {
        key = default;
        uint? found = null;

        foreach (var (id, record) in _records.GetRecordsOfType<GeneralStationRecord>(station))
        {
            if (!string.IsNullOrEmpty(fingerprint) && record.Fingerprint == fingerprint)
            {
                found = id;
                break;
            }

            if (found == null && !string.IsNullOrEmpty(name) && record.Name == name)
                found = id;
        }

        if (found == null)
            return false;

        key = new StationRecordKey(found.Value, station);
        return true;
    }

    /// <summary>The player behind a station record (by its fingerprint/name as seen at spawn).</summary>
    public bool TryGetDebtor(StationRecordKey key, out NetUserId userId)
    {
        userId = default;
        if (!_records.TryGetRecord<GeneralStationRecord>(key, out var general))
            return false;

        return _bankCard.TryGetUser(general.Fingerprint, general.Name, out userId);
    }

    private static CriminalFine? FindFine(CriminalRecord record, uint fineId)
        => record.Fines.FirstOrDefault(f => f.Id == fineId);

    #endregion

    #region Fines

    public bool TryIssue(StationRecordKey key, int amount, string reason, string issuer, out CriminalFine? fine)
    {
        fine = null;
        if (amount <= 0 || !_records.TryGetRecord<CriminalRecord>(key, out var record))
            return false;

        fine = new CriminalFine
        {
            Id = _nextFineId++,
            Amount = amount,
            OriginalAmount = amount,
            Reason = reason,
            IssuerName = issuer,
            IssuedAt = _ticker.RoundDuration(),
        };
        record.Fines.Add(fine);
        _criminal.TryAddHistory(key, Loc.GetString("fine-history-issued", ("amount", amount), ("reason", reason)), issuer);
        _records.Synchronize(key);
        return true;
    }

    /// <summary>Marks a fine as settled and clears a Wanted status once nothing is open anymore.</summary>
    private void Settle(StationRecordKey key, CriminalRecord record, CriminalFine fine, string paidBy)
    {
        fine.Paid = true;
        fine.PaidBy = paidBy;
        _criminal.TryAddHistory(key, Loc.GetString("fine-history-paid", ("amount", fine.OriginalAmount), ("reason", fine.Reason)), paidBy);

        if (record.OpenFineTotal == 0 && record.Status == SecurityStatus.Wanted)
            _criminal.TryChangeStatus(key, SecurityStatus.None, null, Loc.GetString("fine-terminal-name"));

        _records.Synchronize(key);
    }

    /// <summary>Settles a fine from <paramref name="payer"/>; the money goes to Security.</summary>
    public bool TryPay(StationRecordKey key, CriminalFine fine, EconomyParty payer, string paidBy, out EconomyPaymentError error)
    {
        error = EconomyPaymentError.None;
        if (fine.Paid || !_records.TryGetRecord<CriminalRecord>(key, out var record))
            return false;

        if (!_economy.TryTransfer(payer, EconomyParty.Department(key.OriginStation, SecurityAccount), fine.Amount,
                $"fine #{fine.Id} ({fine.Reason}) of {_records.RecordName(key)} paid by {paidBy}", out error))
            return false;

        Settle(key, record, fine, paidBy);
        return true;
    }

    private void OnSalaryDeduction(ref SalaryDeductionEvent ev)
    {
        if (!_cfg.GetCVar(CCVars.EconomyFinesFromSalary))
            return;

        var remaining = ev.Gross - ev.Total;
        if (remaining <= 0)
            return;

        foreach (var stationEnt in _station.GetStations())
        {
            var station = stationEnt.Owner;
            if (!HasComp<StationRecordsComponent>(station))
                continue;

            foreach (var (id, record) in _records.GetRecordsOfType<CriminalRecord>(station).ToList())
            {
                if (record.OpenFineTotal == 0)
                    continue;

                var key = new StationRecordKey(id, station);
                if (!TryGetDebtor(key, out var debtor) || debtor != ev.UserId)
                    continue;

                foreach (var fine in record.Fines)
                {
                    if (fine.Paid || remaining <= 0)
                        continue;

                    var take = (int) Math.Min(fine.Amount, remaining);
                    remaining -= take;
                    ev.Total += take;

                    _economy.TryTransfer(EconomyParty.External, EconomyParty.Department(station, SecurityAccount), take,
                        $"fine #{fine.Id} ({fine.Reason}) of {_records.RecordName(key)} deducted from salary", out _);

                    if (take >= fine.Amount)
                    {
                        Settle(key, record, fine, Loc.GetString("fine-paid-by-salary"));
                        ev.Lines.Add(Loc.GetString("fine-salary-deducted", ("amount", take), ("reason", fine.Reason)));
                    }
                    else
                    {
                        fine.Amount -= take;
                        ev.Lines.Add(Loc.GetString("fine-salary-deducted-partial", ("amount", take), ("remaining", fine.Amount), ("reason", fine.Reason)));
                        _records.Synchronize(key);
                    }
                }
            }
        }
    }

    #endregion

    #region Terminal

    private bool CanIssue(EntityUid user) => _access.FindAccessTags(user).Contains("Security");

    private bool IsLawyer(EntityUid card) => _access.FindAccessTags(card).Contains("Lawyer");

    private void UpdateUi(Entity<FineTerminalComponent> ent, EntityUid? user)
    {
        var station = _station.GetOwningStation(ent);
        var state = new FineTerminalBuiState
        {
            Message = ent.Comp.Message,
            LawyerFeePercent = _cfg.GetCVar(CCVars.EconomyLawyerFeePercent),
        };

        // Permissions are evaluated for whoever opened the UI last; the actor is re-checked on every message.
        var actor = user ?? _ui.GetActors(ent.Owner, FineTerminalUiKey.Key).FirstOrDefault();
        state.CanIssue = actor != default && CanIssue(actor);

        if (station != null && HasComp<StationRecordsComponent>(station))
        {
            if (state.CanIssue)
            {
                foreach (var (id, record) in _records.GetRecordsOfType<CriminalRecord>(station.Value))
                {
                    var name = _records.RecordName(new StationRecordKey(id, station.Value));
                    state.Records.Add(new FineTerminalRecordEntry(id, name, record.OpenFineTotal));
                }

                state.Records.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

                if (ent.Comp.SelectedRecord is { } selected)
                {
                    var key = new StationRecordKey(selected, station.Value);
                    if (_records.TryGetRecord<CriminalRecord>(key, out var record))
                    {
                        state.SelectedRecord = selected;
                        state.SelectedName = _records.RecordName(key);
                        state.SelectedFines = record.Fines.ToList();
                    }
                }
            }

            if (TryGetCard(ent, out var card))
            {
                state.CardHolder = card.Comp.OwnerName;
                if (TryFindRecord(station.Value, card.Comp.OwnerFingerprint, card.Comp.OwnerName, out var ownKey)
                    && _records.TryGetRecord<CriminalRecord>(ownKey, out var own))
                {
                    state.OwnFines = own.Fines.ToList();
                }

                if (IsLawyer(card) && card.Comp.OwnerUserId is { } lawyer)
                {
                    state.IsLawyer = true;
                    var mandate = _mandates.Values.FirstOrDefault(m => m.Lawyer == lawyer);
                    if (mandate != null)
                    {
                        state.ClientName = mandate.ClientName;
                        if (TryFindRecord(station.Value, mandate.ClientFingerprint, mandate.ClientName, out var clientKey)
                            && _records.TryGetRecord<CriminalRecord>(clientKey, out var client))
                        {
                            state.ClientFines = client.Fines.ToList();
                        }
                    }
                }
            }
        }

        if (GetMandatePaper(ent) is { } paper)
        {
            state.MandateStatus = paper.Comp.Filed
                ? Loc.GetString("fine-terminal-mandate-filed", ("client", paper.Comp.ClientName ?? "?"), ("lawyer", paper.Comp.LawyerName ?? "?"))
                : paper.Comp.IsComplete
                    ? Loc.GetString("fine-terminal-mandate-ready", ("client", paper.Comp.ClientName ?? "?"), ("lawyer", paper.Comp.LawyerName ?? "?"))
                    : Loc.GetString("fine-terminal-mandate-incomplete");
        }

        _ui.SetUiState(ent.Owner, FineTerminalUiKey.Key, state);
    }

    private void Fail(Entity<FineTerminalComponent> ent, EntityUid actor, string message)
    {
        ent.Comp.Message = message;
        _audio.PlayPvs(ent.Comp.DenySound, ent);
        UpdateUi(ent, actor);
    }

    private void Succeed(Entity<FineTerminalComponent> ent, EntityUid actor, string message)
    {
        ent.Comp.Message = message;
        _audio.PlayPvs(ent.Comp.PaySound, ent);
        UpdateUi(ent, actor);
    }

    private void OnSelectRecord(Entity<FineTerminalComponent> ent, ref FineTerminalSelectRecordMessage args)
    {
        if (!CanIssue(args.Actor))
            return;

        ent.Comp.SelectedRecord = args.Record;
        UpdateUi(ent, args.Actor);
    }

    private void OnIssue(Entity<FineTerminalComponent> ent, ref FineTerminalIssueMessage args)
    {
        if (!CanIssue(args.Actor))
        {
            Fail(ent, args.Actor, Loc.GetString("fine-terminal-no-access"));
            return;
        }

        if (_station.GetOwningStation(ent) is not { } station)
            return;

        var amount = Math.Clamp(args.Amount, 0, ent.Comp.MaxFine);
        var reason = args.Reason.Trim();
        if (reason.Length > ent.Comp.MaxReasonLength)
            reason = reason[..(int) ent.Comp.MaxReasonLength];

        if (amount <= 0 || reason.Length == 0)
        {
            Fail(ent, args.Actor, Loc.GetString("fine-terminal-invalid"));
            return;
        }

        var key = new StationRecordKey(args.Record, station);
        if (!TryIssue(key, amount, reason, Name(args.Actor), out var fine))
        {
            Fail(ent, args.Actor, Loc.GetString("fine-terminal-no-record"));
            return;
        }

        _adminLog.Add(LogType.Economy, LogImpact.Medium,
            $"{ToPrettyString(args.Actor):player} fined {_records.RecordName(key)} {amount} credits: {reason}");
        Succeed(ent, args.Actor, Loc.GetString("fine-terminal-issued", ("name", _records.RecordName(key)), ("amount", amount)));
    }

    private void OnWaive(Entity<FineTerminalComponent> ent, ref FineTerminalWaiveMessage args)
    {
        if (!CanIssue(args.Actor) || _station.GetOwningStation(ent) is not { } station)
            return;

        var key = new StationRecordKey(args.Record, station);
        if (!_records.TryGetRecord<CriminalRecord>(key, out var record) || FindFine(record, args.Fine) is not { Paid: false } fine)
            return;

        record.Fines.Remove(fine);
        _criminal.TryAddHistory(key, Loc.GetString("fine-history-waived", ("amount", fine.Amount), ("reason", fine.Reason)), Name(args.Actor));
        _records.Synchronize(key);
        _adminLog.Add(LogType.Economy, LogImpact.Medium,
            $"{ToPrettyString(args.Actor):player} waived fine #{fine.Id} ({fine.Amount}) of {_records.RecordName(key)}");
        Succeed(ent, args.Actor, Loc.GetString("fine-terminal-waived"));
    }

    private void OnPayOwn(Entity<FineTerminalComponent> ent, ref FineTerminalPayOwnMessage args)
    {
        if (_station.GetOwningStation(ent) is not { } station || !TryGetCard(ent, out var card))
        {
            Fail(ent, args.Actor, Loc.GetString("fine-terminal-no-card"));
            return;
        }

        if (!_economy.TryGetCardPayer(card, args.Actor, out var payer, out var error))
        {
            Fail(ent, args.Actor, _economy.ErrorText(error));
            return;
        }

        if (!TryFindRecord(station, card.Comp.OwnerFingerprint, card.Comp.OwnerName, out var key)
            || !_records.TryGetRecord<CriminalRecord>(key, out var record)
            || FindFine(record, args.Fine) is not { Paid: false } fine)
        {
            Fail(ent, args.Actor, Loc.GetString("fine-terminal-no-fine"));
            return;
        }

        if (!TryPay(key, fine, payer, card.Comp.OwnerName ?? Name(args.Actor), out error))
        {
            Fail(ent, args.Actor, _economy.ErrorText(error));
            return;
        }

        _adminLog.Add(LogType.Economy, LogImpact.Low, $"{ToPrettyString(args.Actor):player} paid own fine #{fine.Id} ({fine.Amount})");
        Succeed(ent, args.Actor, Loc.GetString("fine-terminal-paid", ("amount", fine.Amount)));
    }

    private void OnFileMandate(Entity<FineTerminalComponent> ent, ref FineTerminalFileMandateMessage args)
    {
        if (GetMandatePaper(ent) is not { } paper)
        {
            Fail(ent, args.Actor, Loc.GetString("fine-terminal-no-mandate"));
            return;
        }

        if (!paper.Comp.IsComplete || paper.Comp.ClientUserId is not { } client || paper.Comp.LawyerUserId is not { } lawyer)
        {
            Fail(ent, args.Actor, Loc.GetString("fine-terminal-mandate-incomplete"));
            return;
        }

        // Filing requires the lawyer's own card in the slot, so a stolen agreement can't be filed by someone else.
        if (!TryGetCard(ent, out var card) || card.Comp.OwnerUserId != lawyer || !IsLawyer(card))
        {
            Fail(ent, args.Actor, Loc.GetString("fine-terminal-mandate-lawyer-card"));
            return;
        }

        // One client per lawyer and one lawyer per client; a newer agreement replaces the older one.
        foreach (var (otherClient, mandate) in _mandates.ToList())
        {
            if (mandate.Lawyer == lawyer)
                _mandates.Remove(otherClient);
        }

        _mandates[client] = new Mandate(lawyer, paper.Comp.LawyerName ?? string.Empty, paper.Comp.ClientName ?? string.Empty, paper.Comp.ClientFingerprint);
        paper.Comp.Filed = true;
        Dirty(paper);

        _adminLog.Add(LogType.Economy, LogImpact.Medium,
            $"{ToPrettyString(args.Actor):player} filed a legal mandate: lawyer {paper.Comp.LawyerName} ({lawyer}) represents {paper.Comp.ClientName} ({client})");
        Succeed(ent, args.Actor, Loc.GetString("fine-terminal-mandate-filed-ok", ("client", paper.Comp.ClientName ?? "?")));
    }

    private void OnPayClient(Entity<FineTerminalComponent> ent, ref FineTerminalPayClientMessage args)
    {
        if (_station.GetOwningStation(ent) is not { } station || !TryGetCard(ent, out var card) || card.Comp.OwnerUserId is not { } lawyer)
        {
            Fail(ent, args.Actor, Loc.GetString("fine-terminal-no-card"));
            return;
        }

        // The lawyer must be the one at the terminal (fingerprint policy applies to their own card).
        var cardError = EconomyPaymentError.None;
        if (!IsLawyer(card) || !_economy.TryGetCardPayer(card, args.Actor, out _, out cardError))
        {
            Fail(ent, args.Actor, cardError == EconomyPaymentError.None ? Loc.GetString("fine-terminal-not-lawyer") : _economy.ErrorText(cardError));
            return;
        }

        var entry = _mandates.FirstOrDefault(kv => kv.Value.Lawyer == lawyer);
        if (entry.Value == null)
        {
            Fail(ent, args.Actor, Loc.GetString("fine-terminal-no-mandate-filed"));
            return;
        }

        var client = entry.Key;
        var mandate = entry.Value;

        if (!TryFindRecord(station, mandate.ClientFingerprint, mandate.ClientName, out var key)
            || !_records.TryGetRecord<CriminalRecord>(key, out var record)
            || FindFine(record, args.Fine) is not { Paid: false } fine)
        {
            Fail(ent, args.Actor, Loc.GetString("fine-terminal-no-fine"));
            return;
        }

        var clientParty = EconomyParty.Player(client);
        var feePercent = Math.Clamp(_cfg.GetCVar(CCVars.EconomyLawyerFeePercent), 0f, 100f);
        var fee = (int) Math.Floor(fine.Amount * feePercent / 100.0);

        if (_economy.GetBalance(clientParty) < fine.Amount + fee)
        {
            Fail(ent, args.Actor, Loc.GetString("fine-terminal-client-insufficient", ("needed", fine.Amount + fee)));
            return;
        }

        var paidBy = Loc.GetString("fine-paid-by-lawyer", ("lawyer", mandate.LawyerName));
        if (!TryPay(key, fine, clientParty, paidBy, out var error))
        {
            Fail(ent, args.Actor, _economy.ErrorText(error));
            return;
        }

        if (fee > 0)
            _economy.TryTransfer(clientParty, EconomyParty.Player(lawyer), fee, $"lawyer fee for fine #{fine.Id} of {mandate.ClientName}", out _);

        _adminLog.Add(LogType.Economy, LogImpact.Medium,
            $"{ToPrettyString(args.Actor):player} (lawyer {lawyer}) paid fine #{fine.Id} ({fine.Amount}) of {mandate.ClientName} ({client}) from the client's account, fee {fee}");
        Succeed(ent, args.Actor, Loc.GetString("fine-terminal-paid-client", ("amount", fine.Amount), ("fee", fee)));
    }

    #endregion

    private sealed record Mandate(NetUserId Lawyer, string LawyerName, string ClientName, string? ClientFingerprint);
}
