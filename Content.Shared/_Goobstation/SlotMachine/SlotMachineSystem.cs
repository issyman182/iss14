// SPDX-License-Identifier: AGPL-3.0-or-later
// Ported from Goobstation.

using System.Linq;
using Content.Shared.Chat;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.DoAfter;
using Content.Shared.Emag.Systems;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Stacks;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Shared._Goobstation.SlotMachine;

public sealed partial class SlotMachineSystem : EntitySystem
{
    /// <summary>
    /// Name of the <see cref="ItemSlotsComponent"/> slot the cash stack is inserted into.
    /// </summary>
    public const string MoneySlotId = "money";

    /// <summary>
    /// Spawned to pay out when the inserted stack was consumed by the spin cost.
    /// </summary>
    private static readonly EntProtoId CashProto = "SpaceCash";

    [Dependency] private EmagSystem _emag = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private INetManager _net = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private ItemSlotsSystem _itemSlots = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private SharedChatSystem _chat = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedPowerReceiverSystem _power = default!;
    [Dependency] private SharedStackSystem _stack = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SlotMachineComponent, ActivateInWorldEvent>(OnActivateInWorld);
        SubscribeLocalEvent<SlotMachineComponent, SlotMachineDoAfterEvent>(OnSlotMachineDoAfter);
        SubscribeLocalEvent<SlotMachineComponent, GotEmaggedEvent>(OnEmagged);
    }

    /// <summary>
    /// Emagging scrambles the payout table, so the house edge becomes anyone's guess.
    /// </summary>
    private void OnEmagged(Entity<SlotMachineComponent> ent, ref GotEmaggedEvent args)
    {
        if (!_emag.CompareFlag(args.Type, EmagType.Interaction))
            return;

        if (_emag.CheckFlag(ent, EmagType.Interaction))
            return;

        args.Handled = true;

        // Only the server rolls; the scrambled values are networked back down.
        if (_net.IsClient)
            return;

        var comp = ent.Comp;
        comp.SpinCost = _random.Next(50, 100000);
        comp.SmallPrizeAmount = _random.Next(-500, 5000);
        comp.MediumPrizeAmount = _random.Next(-500, 10000);
        comp.BigPrizeAmount = _random.Next(-500, 50000);
        comp.JackPotPrizeAmount = _random.Next(-500, 100000);

        comp.SmallWinChance = _random.NextFloat(0f, 0.6f);
        comp.MediumWinChance = _random.NextFloat(0f, 0.35f);
        comp.BigWinChance = _random.NextFloat(0f, 0.2f);
        comp.JackPotWinChance = _random.NextFloat(0f, 0.1f);
        comp.GodPotWinChance = _random.NextFloat(0f, 0.05f);

        // lord have mercy...
        var spawnable = _proto.EnumeratePrototypes<EntityPrototype>()
            .Where(p => !p.Abstract && !p.HideSpawnMenu)
            .ToList();

        if (spawnable.Count > 0)
            comp.GodPotPrize = _random.Pick(spawnable).ID;

        Dirty(ent);
    }

    private void OnActivateInWorld(Entity<SlotMachineComponent> ent, ref ActivateInWorldEvent args)
    {
        if (args.Handled || ent.Comp.IsSpinning || !_power.IsPowered(ent.Owner))
            return;

        if (!_itemSlots.TryGetSlot(ent.Owner, MoneySlotId, out var slot)
            || slot.Item is not { } money
            || !TryComp<StackComponent>(money, out var stack)
            || stack.Count < ent.Comp.SpinCost)
        {
            _popup.PopupEntity(Loc.GetString("slotmachine-no-money"), ent, args.User, PopupType.Small);
            return;
        }

        args.Handled = true;

        _stack.SetCount((money, stack), stack.Count - ent.Comp.SpinCost);

        ent.Comp.IsSpinning = true;
        Dirty(ent);

        if (_net.IsClient)
            return;

        _audio.PlayPvs(ent.Comp.SpinSound, ent);
        _appearance.SetData(ent, SlotMachineVisuals.Spinning, true);

        _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, ent, ent.Comp.DoAfterTime, new SlotMachineDoAfterEvent(), ent)
        {
            BreakOnMove = false,
            BreakOnDamage = false,
            MultiplyDelay = false,
        });
    }

    private void OnSlotMachineDoAfter(Entity<SlotMachineComponent> ent, ref SlotMachineDoAfterEvent args)
    {
        ent.Comp.IsSpinning = false;
        Dirty(ent);

        if (_net.IsClient)
            return;

        _appearance.SetData(ent, SlotMachineVisuals.Spinning, false);

        // Almost no way for it to be cancelled, but just in case.
        if (args.Cancelled || args.Handled)
            return;

        args.Handled = true;

        // The stack may have been emptied by the spin cost, in which case HandlePrize spawns a fresh one.
        Entity<StackComponent>? stack = null;
        if (_itemSlots.TryGetSlot(ent.Owner, MoneySlotId, out var slot)
            && slot.Item is { } money
            && TryComp<StackComponent>(money, out var moneyStack))
        {
            stack = (money, moneyStack);
        }

        if (_random.Prob(ent.Comp.SmallWinChance))
        {
            _audio.PlayPvs(ent.Comp.SmallWinSound, ent);
            HandlePrize(ent, Loc.GetString("slotmachine-win-normal", ("amount", ent.Comp.SmallPrizeAmount)), stack, ent.Comp.SmallPrizeAmount);
            return;
        }

        if (_random.Prob(ent.Comp.MediumWinChance))
        {
            _audio.PlayPvs(ent.Comp.MediumWinSound, ent);
            HandlePrize(ent, Loc.GetString("slotmachine-win-normal", ("amount", ent.Comp.MediumPrizeAmount)), stack, ent.Comp.MediumPrizeAmount);
            return;
        }

        if (_random.Prob(ent.Comp.BigWinChance))
        {
            _audio.PlayPvs(ent.Comp.BigWinSound, ent);
            HandlePrize(ent, Loc.GetString("slotmachine-win-normal", ("amount", ent.Comp.BigPrizeAmount)), stack, ent.Comp.BigPrizeAmount);
            return;
        }

        if (_random.Prob(ent.Comp.JackPotWinChance))
        {
            _audio.PlayPvs(ent.Comp.JackPotWinSound, ent);
            HandlePrize(ent, Loc.GetString("slotmachine-win-jackpot"), stack, ent.Comp.JackPotPrizeAmount);
            return;
        }

        if (_random.Prob(ent.Comp.GodPotWinChance)) // THE GODPOT!!!
        {
            _audio.PlayPvs(ent.Comp.GodPotWinSound, ent);
            Spawn(ent.Comp.GodPotPrize, Transform(ent).Coordinates);
            Announce(ent, Loc.GetString("slotmachine-win-godpot"));
            return;
        }

        _audio.PlayPvs(ent.Comp.LoseSound, ent); // If nothing, then lose.
    }

    private void HandlePrize(EntityUid uid, string msg, Entity<StackComponent>? stack, int prize)
    {
        if (stack is { } cash)
        {
            _stack.SetCount(cash.AsNullable(), cash.Comp.Count + prize);
        }
        else
        {
            // Spawn a new cash stack if there's no money left in the machine.
            var newStack = Spawn(CashProto, Transform(uid).Coordinates);
            _stack.SetCount((newStack, null), prize);
        }

        Announce(uid, msg);
    }

    private void Announce(EntityUid uid, string msg)
    {
        _chat.TrySendInGameICMessage(uid, msg, InGameICChatType.Speak, hideChat: false, hideLog: true, checkRadioPrefix: false);
    }
}
