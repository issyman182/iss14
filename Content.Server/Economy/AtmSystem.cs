// iss14: Economy
using Content.Server.Administration.Logs;
using Content.Server.Stack;
using Content.Shared.Access.Components;
using Content.Shared.CCVar;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Database;
using Content.Shared.Economy;
using Content.Shared.GameTicking;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Stacks;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server.Economy;

/// <summary>
/// Bank terminal logic. The account is resolved from the <em>inserted</em> ID card (<see cref="BankCardComponent"/>),
/// so a stolen card gives access to its owner's money unless <c>economy.atm_require_fingerprint</c> is on.
/// </summary>
public sealed partial class AtmSystem : EntitySystem
{
    [Dependency] private IAdminLogManager _adminLog = default!;
    [Dependency] private IBankManager _bank = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private BankCardSystem _cards = default!;
    [Dependency] private ItemSlotsSystem _slots = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedPowerReceiverSystem _power = default!;
    [Dependency] private StackSystem _stack = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;

    /// <summary>Credits withdrawn this round per acting player (the person at the terminal, not the card owner).</summary>
    private readonly Dictionary<NetUserId, long> _withdrawnThisRound = new();

    /// <summary>Balances fetched from the database for cards whose owner is offline, per ATM.</summary>
    private readonly Dictionary<EntityUid, long> _offlineBalances = new();

    public override void Initialize()
    {
        base.Initialize();

        Subs.BuiEvents<AtmComponent>(AtmUiKey.Key, subs =>
        {
            subs.Event<BoundUIOpenedEvent>(OnUiOpened);
            subs.Event<AtmWithdrawMessage>(OnWithdraw);
            subs.Event<AtmDepositMessage>(OnDeposit);
            subs.Event<AtmVerifyHolderMessage>(OnVerify);
        });

        SubscribeLocalEvent<AtmComponent, EntInsertedIntoContainerMessage>(OnSlotChanged);
        SubscribeLocalEvent<AtmComponent, EntRemovedFromContainerMessage>(OnSlotChanged);
        SubscribeLocalEvent<AtmComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<BankBalanceChangedEvent>(OnBalanceChanged);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        _withdrawnThisRound.Clear();
        _offlineBalances.Clear();
    }

    private void OnShutdown(Entity<AtmComponent> ent, ref ComponentShutdown args)
    {
        _offlineBalances.Remove(ent);
    }

    private void OnBalanceChanged(BankBalanceChangedEvent ev)
    {
        var query = EntityQueryEnumerator<AtmComponent>();
        while (query.MoveNext(out var uid, out var atm))
        {
            if (TryGetCard((uid, atm), out var card) && card.Comp.OwnerUserId == ev.UserId)
                UpdateUi((uid, atm));
        }
    }

    private void OnUiOpened(Entity<AtmComponent> ent, ref BoundUIOpenedEvent args)
    {
        UpdateUi(ent);
    }

    private void OnSlotChanged(Entity<AtmComponent> ent, ref EntInsertedIntoContainerMessage args)
    {
        if (args.Container.ID == AtmComponent.IdSlotId)
            OnCardChanged(ent);

        UpdateUi(ent);
    }

    private void OnSlotChanged(Entity<AtmComponent> ent, ref EntRemovedFromContainerMessage args)
    {
        if (args.Container.ID == AtmComponent.IdSlotId)
            OnCardChanged(ent);

        UpdateUi(ent);
    }

    private void OnCardChanged(Entity<AtmComponent> ent)
    {
        ent.Comp.LastVerify = null;
        ent.Comp.Message = string.Empty;
        _offlineBalances.Remove(ent);

        if (TryGetCard(ent, out var card) && card.Comp.OwnerUserId is { } owner && !_bank.IsLoaded(owner))
            FetchOfflineBalance(ent, owner);
    }

    private async void FetchOfflineBalance(Entity<AtmComponent> ent, NetUserId owner)
    {
        try
        {
            var balance = await _bank.GetBalanceAsync(owner);
            if (Deleted(ent) || !TryGetCard(ent, out var card) || card.Comp.OwnerUserId != owner)
                return;

            _offlineBalances[ent] = balance;
            UpdateUi(ent);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to fetch offline balance for {owner}: {e}");
        }
    }

    #region Lookups

    /// <summary>The bound bank card in the ID slot, if any.</summary>
    public bool TryGetCard(Entity<AtmComponent> ent, out Entity<BankCardComponent> card)
    {
        card = default;
        if (!_slots.TryGetSlot(ent.Owner, AtmComponent.IdSlotId, out var slot) || slot.Item is not { } item)
            return false;

        if (!TryComp<BankCardComponent>(item, out var bank) || bank.OwnerUserId == null)
            return false;

        card = (item, bank);
        return true;
    }

    /// <summary>The credit stack in the cash slot, if any.</summary>
    public bool TryGetCash(Entity<AtmComponent> ent, out Entity<StackComponent> cash)
    {
        cash = default;
        if (!_slots.TryGetSlot(ent.Owner, AtmComponent.CashSlotId, out var slot) || slot.Item is not { } item)
            return false;

        if (!TryComp<StackComponent>(item, out var stack))
            return false;

        cash = (item, stack);
        return true;
    }

    private long GetBalance(Entity<AtmComponent> ent, NetUserId owner)
    {
        if (_bank.IsLoaded(owner))
            return _bank.GetBalance(owner);

        return _offlineBalances.GetValueOrDefault(ent.Owner);
    }

    private string OwnerName(Entity<BankCardComponent> card)
    {
        if (!string.IsNullOrEmpty(card.Comp.OwnerName))
            return card.Comp.OwnerName;

        if (TryComp<IdCardComponent>(card, out var id) && !string.IsNullOrEmpty(id.FullName))
            return id.FullName;

        return Loc.GetString("atm-unknown-owner");
    }

    private int WithdrawRemaining(EntityUid user)
    {
        var limit = _cfg.GetCVar(CCVars.EconomyAtmWithdrawLimitPerRound);
        if (limit <= 0)
            return -1;

        if (!TryComp<ActorComponent>(user, out var actor))
            return limit;

        return (int) Math.Max(0, limit - _withdrawnThisRound.GetValueOrDefault(actor.PlayerSession.UserId));
    }

    #endregion

    #region Actions

    private void OnWithdraw(Entity<AtmComponent> ent, ref AtmWithdrawMessage args)
    {
        TryWithdraw(ent, args.Actor, args.Amount);
    }

    /// <summary>Withdraws <paramref name="amount"/> credits as a cash stack into the user's hand (or onto the ATM).</summary>
    public bool TryWithdraw(Entity<AtmComponent> ent, EntityUid user, int amount)
    {
        if (!_power.IsPowered(ent.Owner))
            return Fail(ent, user, "atm-no-power");

        if (!TryGetCard(ent, out var card) || card.Comp.OwnerUserId is not { } owner)
            return Fail(ent, user, "atm-no-card");

        if (amount <= 0)
            return Fail(ent, user, "atm-bad-amount");

        if (_cfg.GetCVar(CCVars.EconomyAtmRequireFingerprint))
        {
            var verify = _cards.VerifyHolder(card, user);
            ent.Comp.LastVerify = verify;
            if (verify != AtmVerifyResult.Match)
                return Fail(ent, user, "atm-fingerprint-rejected");
        }

        var remaining = WithdrawRemaining(user);
        if (remaining >= 0 && amount > remaining)
            return Fail(ent, user, "atm-limit-reached", ("remaining", remaining));

        var feePercent = Math.Max(0f, _cfg.GetCVar(CCVars.EconomyAtmFeePercent));
        var fee = (long) Math.Ceiling(amount * feePercent / 100.0);
        var total = amount + fee;

        if (GetBalance(ent, owner) < total)
            return Fail(ent, user, "atm-insufficient-funds");

        var reason = $"ATM withdrawal by {ToPrettyString(user)} with card {ToPrettyString(card)}" + (fee > 0 ? $" (fee {fee})" : "");

        if (_bank.IsLoaded(owner))
        {
            if (!_bank.TryDebit(owner, total, reason, out _))
                return Fail(ent, user, "atm-insufficient-funds");

            CompleteWithdraw(ent, user, card, amount, fee);
            return true;
        }

        WithdrawOffline(ent, user, card, owner, amount, fee, reason);
        return true;
    }

    private async void WithdrawOffline(Entity<AtmComponent> ent, EntityUid user, Entity<BankCardComponent> card, NetUserId owner, int amount, long fee, string reason)
    {
        try
        {
            var result = await _bank.AdjustBalanceAsync(owner, -(amount + fee), reason);
            if (Deleted(ent))
                return;

            if (result == null)
            {
                Fail(ent, user, "atm-insufficient-funds");
                return;
            }

            _offlineBalances[ent] = result.Value;
            CompleteWithdraw(ent, user, card, amount, fee);
        }
        catch (Exception e)
        {
            Log.Error($"Offline ATM withdrawal failed for {owner}: {e}");
        }
    }

    private void CompleteWithdraw(Entity<AtmComponent> ent, EntityUid user, Entity<BankCardComponent> card, int amount, long fee)
    {
        if (TryComp<ActorComponent>(user, out var actor))
        {
            var id = actor.PlayerSession.UserId;
            _withdrawnThisRound[id] = _withdrawnThisRound.GetValueOrDefault(id) + amount;
        }

        var cash = Spawn(ent.Comp.CashPrototype, Transform(ent).Coordinates);
        _stack.SetCount((cash, null), amount);
        _hands.PickupOrDrop(user, cash, animateUser: false, dropNear: true);

        _adminLog.Add(LogType.Economy, LogImpact.Low,
            $"{ToPrettyString(user):user} withdrew {amount} credits (fee {fee}) from {ToPrettyString(ent):atm} using {ToPrettyString(card):card} (account {card.Comp.OwnerUserId})");

        _audio.PlayPvs(ent.Comp.WithdrawSound, ent);
        ent.Comp.Message = fee > 0
            ? Loc.GetString("atm-withdrew-fee", ("amount", amount), ("fee", fee))
            : Loc.GetString("atm-withdrew", ("amount", amount));
        UpdateUi(ent);
    }

    private void OnDeposit(Entity<AtmComponent> ent, ref AtmDepositMessage args)
    {
        TryDeposit(ent, args.Actor);
    }

    /// <summary>Credits the cash stack in the cash slot to the card's account and consumes it.</summary>
    public bool TryDeposit(Entity<AtmComponent> ent, EntityUid user)
    {
        if (!_power.IsPowered(ent.Owner))
            return Fail(ent, user, "atm-no-power");

        if (!TryGetCard(ent, out var card) || card.Comp.OwnerUserId is not { } owner)
            return Fail(ent, user, "atm-no-card");

        if (!TryGetCash(ent, out var cash) || cash.Comp.Count <= 0)
            return Fail(ent, user, "atm-no-cash");

        var amount = cash.Comp.Count;
        var reason = $"ATM deposit by {ToPrettyString(user)} with card {ToPrettyString(card)}";

        if (_bank.IsLoaded(owner))
        {
            var max = _cfg.GetCVar(CCVars.EconomyMaxBalance);
            if (max > 0 && _bank.GetBalance(owner) + amount > max)
                return Fail(ent, user, "atm-account-full");

            if (!_bank.Credit(owner, amount, reason, out _))
                return Fail(ent, user, "atm-error");

            CompleteDeposit(ent, user, card, cash, amount);
            return true;
        }

        DepositOffline(ent, user, card, owner, cash, amount, reason);
        return true;
    }

    private async void DepositOffline(Entity<AtmComponent> ent, EntityUid user, Entity<BankCardComponent> card, NetUserId owner, Entity<StackComponent> cash, int amount, string reason)
    {
        try
        {
            // Take the cash first so it cannot be pulled out of the slot while the write is in flight.
            if (Deleted(cash) || cash.Comp.Count != amount)
                return;

            QueueDel(cash);

            var result = await _bank.AdjustBalanceAsync(owner, amount, reason);
            if (Deleted(ent))
                return;

            if (result != null)
                _offlineBalances[ent] = result.Value;

            CompleteDeposit(ent, user, card, null, amount);
        }
        catch (Exception e)
        {
            Log.Error($"Offline ATM deposit failed for {owner}: {e}");
        }
    }

    private void CompleteDeposit(Entity<AtmComponent> ent, EntityUid user, Entity<BankCardComponent> card, Entity<StackComponent>? cash, int amount)
    {
        if (cash != null)
            QueueDel(cash.Value);

        _adminLog.Add(LogType.Economy, LogImpact.Low,
            $"{ToPrettyString(user):user} deposited {amount} credits at {ToPrettyString(ent):atm} using {ToPrettyString(card):card} (account {card.Comp.OwnerUserId})");

        _audio.PlayPvs(ent.Comp.DepositSound, ent);
        ent.Comp.Message = Loc.GetString("atm-deposited", ("amount", amount));
        UpdateUi(ent);
    }

    private void OnVerify(Entity<AtmComponent> ent, ref AtmVerifyHolderMessage args)
    {
        if (!TryGetCard(ent, out var card))
        {
            Fail(ent, args.Actor, "atm-no-card");
            return;
        }

        ent.Comp.LastVerify = _cards.VerifyHolder(card, args.Actor);
        ent.Comp.Message = string.Empty;
        UpdateUi(ent);
    }

    private bool Fail(Entity<AtmComponent> ent, EntityUid user, string locId, params (string, object)[] args)
    {
        ent.Comp.Message = Loc.GetString(locId, args);
        _audio.PlayPvs(ent.Comp.DenySound, ent);
        UpdateUi(ent);
        return false;
    }

    #endregion

    public void UpdateUi(Entity<AtmComponent> ent)
    {
        var hasCard = TryGetCard(ent, out var card);
        var ownerName = hasCard ? OwnerName(card) : null;
        var balance = hasCard && card.Comp.OwnerUserId is { } owner ? GetBalance(ent, owner) : 0;
        var cashInSlot = TryGetCash(ent, out var cash) ? cash.Comp.Count : 0;

        // The per-round limit is per acting player; the UI shows the global limit and lets the server
        // enforce the exact remainder, since one ATM may be used by several people.
        var limit = _cfg.GetCVar(CCVars.EconomyAtmWithdrawLimitPerRound);

        var state = new AtmBoundUserInterfaceState(
            hasCard,
            ownerName,
            balance,
            cashInSlot,
            limit <= 0 ? -1 : limit,
            _cfg.GetCVar(CCVars.EconomyAtmFeePercent),
            _cfg.GetCVar(CCVars.EconomyAtmRequireFingerprint),
            ent.Comp.LastVerify,
            ent.Comp.Message);

        _ui.SetUiState(ent.Owner, AtmUiKey.Key, state);
    }

    /// <summary>Testing hook: how much the player withdrew this round.</summary>
    public long GetWithdrawnThisRound(NetUserId user) => _withdrawnThisRound.GetValueOrDefault(user);
}
