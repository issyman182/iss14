// iss14: Economy
using Content.Server.Stack;
using Content.Shared.CCVar;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Economy;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Stacks;
using Content.Shared.Verbs;
using Robust.Shared.Audio.Systems;
using Content.Shared.GameTicking;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Utility;

namespace Content.Server.Economy;

/// <summary>
/// Poker chip exchange: spesos in the cash slot become chips in the chip slot (and back) at
/// <c>economy.chip_rate</c> credits per chip. Nothing is created from thin air: cash is destroyed when chips are
/// minted and chips are destroyed when cash is paid out. Cashing out costs <c>economy.chip_exchange_fee_percent</c>
/// (the house keeps it) and is capped per player and round by <c>economy.chip_payout_limit_per_round</c>.
/// </summary>
public sealed partial class ChipExchangerSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private ItemSlotsSystem _slots = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedPowerReceiverSystem _power = default!;
    [Dependency] private StackSystem _stack = default!;
    [Dependency] private EconomySystem _economy = default!;
    [Dependency] private IPlayerManager _players = default!;

    /// <summary>Credits already cashed out per player this round.</summary>
    private readonly Dictionary<NetUserId, long> _paidOut = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _paidOut.Clear());

        SubscribeLocalEvent<ChipExchangerComponent, GetVerbsEvent<ActivationVerb>>(OnGetVerbs);
        SubscribeLocalEvent<ChipExchangerComponent, ActivateInWorldEvent>(OnActivate);
    }

    private void OnGetVerbs(Entity<ChipExchangerComponent> ent, ref GetVerbsEvent<ActivationVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;

        var user = args.User;
        args.Verbs.Add(new ActivationVerb
        {
            Text = Loc.GetString("chip-exchanger-verb-cash-to-chips"),
            Icon = new SpriteSpecifier.Texture(new ResPath("/Textures/_Goobstation/Objects/Fun/poker_chips.rsi/chips5.png")),
            Act = () => TryCashToChips(ent, user),
            Priority = 2,
        });
        args.Verbs.Add(new ActivationVerb
        {
            Text = Loc.GetString("chip-exchanger-verb-chips-to-cash"),
            Icon = new SpriteSpecifier.Texture(new ResPath("/Textures/Objects/Economy/cash.rsi/cash.png")),
            Act = () => TryChipsToCash(ent, user),
            Priority = 1,
        });
    }

    /// <summary>Plain activation: convert whichever slot has something, cash first.</summary>
    private void OnActivate(Entity<ChipExchangerComponent> ent, ref ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex)
            return;

        args.Handled = true;

        if (GetStack(ent, ChipExchangerComponent.CashSlotId) != null)
            TryCashToChips(ent, args.User);
        else
            TryChipsToCash(ent, args.User);
    }

    private Entity<StackComponent>? GetStack(Entity<ChipExchangerComponent> ent, string slotId)
    {
        if (!_slots.TryGetSlot(ent.Owner, slotId, out var slot) || slot.Item is not { } item)
            return null;

        if (!TryComp<StackComponent>(item, out var stack) || stack.Count <= 0)
            return null;

        return (item, stack);
    }

    private int Rate => Math.Max(1, _cfg.GetCVar(CCVars.EconomyChipRate));

    /// <summary>Turns the whole cash stack into chips (rounding down; the remainder stays in the cash slot).</summary>
    public bool TryCashToChips(Entity<ChipExchangerComponent> ent, EntityUid user)
    {
        if (!_power.IsPowered(ent.Owner))
            return Fail(ent, user, "chip-exchanger-no-power");

        if (GetStack(ent, ChipExchangerComponent.CashSlotId) is not { } cash)
            return Fail(ent, user, "chip-exchanger-no-cash");

        var rate = Rate;
        var chips = cash.Comp.Count / rate;
        if (chips <= 0)
            return Fail(ent, user, "chip-exchanger-too-little-cash", ("rate", rate));

        var spent = chips * rate;
        _stack.SetCount(cash.AsNullable(), cash.Comp.Count - spent);
        if (cash.Comp.Count <= 0)
            QueueDel(cash);

        var minted = Spawn(ent.Comp.ChipPrototype, Transform(ent).Coordinates);
        _stack.SetCount((minted, null), chips);
        Deliver(ent, user, minted, ChipExchangerComponent.ChipSlotId);

        _audio.PlayPvs(ent.Comp.ExchangeSound, ent);
        _popup.PopupEntity(Loc.GetString("chip-exchanger-bought", ("chips", chips), ("credits", spent)), ent, user);
        return true;
    }

    /// <summary>Turns the whole chip stack into cash.</summary>
    public bool TryChipsToCash(Entity<ChipExchangerComponent> ent, EntityUid user)
    {
        if (!_power.IsPowered(ent.Owner))
            return Fail(ent, user, "chip-exchanger-no-power");

        if (GetStack(ent, ChipExchangerComponent.ChipSlotId) is not { } chips)
            return Fail(ent, user, "chip-exchanger-no-chips");

        var rate = Rate;
        var count = chips.Comp.Count;

        // Per-round cash-out cap: only as many chips as still fit under the limit.
        var limit = _cfg.GetCVar(CCVars.EconomyChipPayoutLimitPerRound);
        NetUserId? userId = _players.TryGetSessionByEntity(user, out var session) ? session.UserId : null;
        if (limit > 0 && userId != null)
        {
            var remaining = limit - _paidOut.GetValueOrDefault(userId.Value);
            var allowed = (int) Math.Max(0, remaining / rate);
            if (allowed <= 0)
                return Fail(ent, user, "chip-exchanger-limit-reached", ("limit", limit));

            count = Math.Min(count, allowed);
        }

        var gross = (long) count * rate;
        var feePercent = Math.Clamp(_cfg.GetCVar(CCVars.EconomyChipExchangeFeePercent), 0f, 100f);
        var fee = (int) Math.Ceiling(gross * feePercent / 100.0);
        var credits = (int) gross - fee;
        if (credits <= 0)
            return Fail(ent, user, "chip-exchanger-too-few-chips");

        if (count >= chips.Comp.Count)
            QueueDel(chips);
        else
            _stack.SetCount(chips.AsNullable(), chips.Comp.Count - count);

        if (userId != null)
            _paidOut[userId.Value] = _paidOut.GetValueOrDefault(userId.Value) + gross;

        var cash = Spawn(ent.Comp.CashPrototype, Transform(ent).Coordinates);
        _stack.SetCount((cash, null), credits);
        Deliver(ent, user, cash, ChipExchangerComponent.CashSlotId);

        if (fee > 0)
            _economy.Record(EconomyParty.External, EconomyParty.External, fee, $"chip exchange fee from {ToPrettyString(user)} at {ToPrettyString(ent)}");

        _audio.PlayPvs(ent.Comp.ExchangeSound, ent);
        _popup.PopupEntity(Loc.GetString("chip-exchanger-sold", ("chips", count), ("credits", credits), ("fee", fee)), ent, user);
        return true;
    }

    /// <summary>Puts the output into its slot if that slot is free, otherwise into the user's hands / on the floor.</summary>
    private void Deliver(Entity<ChipExchangerComponent> ent, EntityUid user, EntityUid output, string slotId)
    {
        if (_slots.TryGetSlot(ent.Owner, slotId, out var slot) && !slot.HasItem && _slots.TryInsert(ent.Owner, slot, output, user))
            return;

        // Slot occupied (e.g. leftover cash): hand it over instead.
        _hands.PickupOrDrop(user, output, animateUser: false, dropNear: true);
    }

    private bool Fail(Entity<ChipExchangerComponent> ent, EntityUid user, string locId, params (string, object)[] args)
    {
        _popup.PopupEntity(Loc.GetString(locId, args), ent, user, PopupType.SmallCaution);
        _audio.PlayPvs(ent.Comp.DenySound, ent);
        return false;
    }
}
