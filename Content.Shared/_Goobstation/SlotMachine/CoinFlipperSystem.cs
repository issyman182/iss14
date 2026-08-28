// SPDX-License-Identifier: AGPL-3.0-or-later
// Ported from Goobstation (there: CoinFlipperMachineSystem).

using Content.Shared.Chat;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Stacks;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Network;
using Robust.Shared.Random;
using Robust.Shared.Serialization;

namespace Content.Shared._Goobstation.SlotMachine;

public sealed partial class CoinFlipperSystem : EntitySystem
{
    [Dependency] private INetManager _net = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private ItemSlotsSystem _itemSlots = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private SharedChatSystem _chat = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedPowerReceiverSystem _power = default!;
    [Dependency] private SharedStackSystem _stack = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CoinFlipperComponent, ActivateInWorldEvent>(OnActivateInWorld);
        SubscribeLocalEvent<CoinFlipperComponent, CoinFlipperDoAfterEvent>(OnCoinFlipperDoAfter);
    }

    private void OnActivateInWorld(Entity<CoinFlipperComponent> ent, ref ActivateInWorldEvent args)
    {
        if (args.Handled || ent.Comp.IsSpinning || !_power.IsPowered(ent.Owner))
            return;

        if (!_itemSlots.TryGetSlot(ent.Owner, SlotMachineSystem.MoneySlotId, out var slot)
            || slot.Item is not { } money
            || !TryComp<StackComponent>(money, out var stack)
            || stack.Count <= 0)
        {
            _popup.PopupEntity(Loc.GetString("slotmachine-no-money"), ent, args.User, PopupType.Small);
            return;
        }

        args.Handled = true;

        // The whole stack is the wager; taking it to zero deletes the stack.
        ent.Comp.PrizeAmount = stack.Count;
        _stack.SetCount((money, stack), 0);

        ent.Comp.IsSpinning = true;
        Dirty(ent);

        if (_net.IsClient)
            return;

        _audio.PlayPvs(ent.Comp.SpinSound, ent);

        _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, ent, ent.Comp.DoAfterTime, new CoinFlipperDoAfterEvent(), ent)
        {
            BreakOnMove = false,
            BreakOnDamage = false,
            MultiplyDelay = false,
        });
    }

    private void OnCoinFlipperDoAfter(Entity<CoinFlipperComponent> ent, ref CoinFlipperDoAfterEvent args)
    {
        ent.Comp.IsSpinning = false;
        Dirty(ent);

        if (_net.IsClient || args.Cancelled || args.Handled)
            return;

        args.Handled = true;

        if (_random.Prob(ent.Comp.WinChance))
        {
            var prize = ent.Comp.PrizeAmount * 2;
            _audio.PlayPvs(ent.Comp.WinSound, ent);
            var newStack = Spawn("SpaceCash", Transform(ent).Coordinates);
            _stack.SetCount((newStack, null), prize);
            _chat.TrySendInGameICMessage(ent, Loc.GetString("coinflipper-win", ("amount", prize)), InGameICChatType.Speak, hideChat: false, hideLog: true, checkRadioPrefix: false);
        }
        else
        {
            _audio.PlayPvs(ent.Comp.LoseSound, ent);
        }

        ent.Comp.PrizeAmount = 0;
        Dirty(ent);
    }
}

[Serializable, NetSerializable]
public sealed partial class CoinFlipperDoAfterEvent : SimpleDoAfterEvent;
