// SPDX-License-Identifier: AGPL-3.0-or-later
// Ported from Goobstation.

using Content.Shared.DoAfter;
using Content.Shared.Emag.Systems;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Network;
using Robust.Shared.Random;
using Robust.Shared.Serialization;

namespace Content.Shared._Goobstation.SlotMachine;

public sealed partial class ClawMachineSystem : EntitySystem
{
    [Dependency] private EmagSystem _emag = default!;
    [Dependency] private INetManager _net = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedPowerReceiverSystem _power = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ClawMachineComponent, ActivateInWorldEvent>(OnActivateInWorld);
        SubscribeLocalEvent<ClawMachineComponent, ClawGameDoAfterEvent>(OnClawGameDoAfter);
        SubscribeLocalEvent<ClawMachineComponent, GotEmaggedEvent>(OnEmagged);
    }

    private void OnEmagged(Entity<ClawMachineComponent> ent, ref GotEmaggedEvent args)
    {
        if (!_emag.CompareFlag(args.Type, EmagType.Interaction))
            return;

        if (_emag.CheckFlag(ent, EmagType.Interaction))
            return;

        args.Handled = true;

        ent.Comp.Rewards = ent.Comp.EvilRewards; // My name is nhoj nhoj and I am EVIL
        Dirty(ent);
    }

    private void OnActivateInWorld(Entity<ClawMachineComponent> ent, ref ActivateInWorldEvent args)
    {
        if (args.Handled || ent.Comp.IsSpinning || !_power.IsPowered(ent.Owner))
            return;

        args.Handled = true;

        ent.Comp.IsSpinning = true;
        Dirty(ent);

        if (_net.IsClient)
            return;

        _audio.PlayPvs(ent.Comp.PlaySound, ent);
        _appearance.SetData(ent, ClawMachineVisuals.Spinning, true);
        _appearance.SetData(ent, ClawMachineVisuals.NormalSprite, false);

        // Unlike the other machines the doafter runs on the PLAYER: moving or getting hit
        // makes them fumble the claw.
        _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, args.User, ent.Comp.DoAfterTime, new ClawGameDoAfterEvent(), ent)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            MultiplyDelay = false,
        });
    }

    private void OnClawGameDoAfter(Entity<ClawMachineComponent> ent, ref ClawGameDoAfterEvent args)
    {
        ent.Comp.IsSpinning = false;
        Dirty(ent);

        if (_net.IsClient || args.Handled)
            return;

        args.Handled = true;

        _appearance.SetData(ent, ClawMachineVisuals.Spinning, false);
        _appearance.SetData(ent, ClawMachineVisuals.NormalSprite, true);

        if (args.Cancelled)
        {
            _popup.PopupEntity(Loc.GetString("clawmachine-fail-self"), ent, args.User, PopupType.Small);
            _popup.PopupEntity(Loc.GetString("clawmachine-fail-other", ("user", args.User)), ent, Robust.Shared.Player.Filter.PvsExcept(args.User), true, PopupType.Small);
            return;
        }

        if (ent.Comp.Rewards is { Count: > 0 } rewards && _random.Prob(ent.Comp.WinChance))
        {
            _audio.PlayPvs(ent.Comp.WinSound, ent);
            Spawn(_random.Pick(rewards), Transform(ent).Coordinates);
            return;
        }

        _popup.PopupEntity(Loc.GetString("clawmachine-fail-generic"), ent);
        _audio.PlayPvs(ent.Comp.LoseSound, ent);
    }
}

[Serializable, NetSerializable]
public sealed partial class ClawGameDoAfterEvent : SimpleDoAfterEvent;
