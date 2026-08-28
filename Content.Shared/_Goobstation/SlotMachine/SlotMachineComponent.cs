// SPDX-License-Identifier: AGPL-3.0-or-later
// Ported from Goobstation.

using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Goobstation.SlotMachine;

/// <summary>
/// A gambling machine. Insert a cash stack, activate it, and the machine rolls for a prize
/// which is paid straight back into the inserted stack.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class SlotMachineComponent : Component
{
    #region Sounds

    [DataField]
    public SoundSpecifier SpinSound = new SoundPathSpecifier("/Audio/_Goobstation/Machines/SlotMachine/slotmachine_spin.ogg");

    [DataField]
    public SoundSpecifier LoseSound = new SoundPathSpecifier("/Audio/Machines/buzz-two.ogg");

    [DataField]
    public SoundSpecifier SmallWinSound = new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg");

    [DataField]
    public SoundSpecifier MediumWinSound = new SoundPathSpecifier("/Audio/Effects/Arcade/win.ogg");

    [DataField]
    public SoundSpecifier BigWinSound = new SoundPathSpecifier("/Audio/_Goobstation/Machines/SlotMachine/slotmachine_bigwin.ogg");

    [DataField]
    public SoundSpecifier JackPotWinSound = new SoundPathSpecifier("/Audio/_Goobstation/Machines/SlotMachine/slotmachine_jackpotwin.ogg");

    [DataField]
    public SoundSpecifier GodPotWinSound = new SoundPathSpecifier("/Audio/_Goobstation/Machines/SlotMachine/slotmachine_godpot.ogg");

    #endregion

    #region Chances

    [DataField, AutoNetworkedField]
    public float SmallWinChance = .20f;

    [DataField, AutoNetworkedField]
    public float MediumWinChance = .10f;

    [DataField, AutoNetworkedField]
    public float BigWinChance = .05f;

    [DataField, AutoNetworkedField]
    public float JackPotWinChance = .01f;

    [DataField, AutoNetworkedField]
    public float GodPotWinChance = .0001f;

    #endregion

    /// <summary>
    /// Spawned on the machine's tile when the god pot hits.
    /// </summary>
    [DataField, AutoNetworkedField]
    public EntProtoId GodPotPrize = "WeaponShotgunKammerer";

    #region Prize Amounts

    [DataField, AutoNetworkedField]
    public int SpinCost = 250;

    [DataField, AutoNetworkedField]
    public int SmallPrizeAmount = 250;

    [DataField, AutoNetworkedField]
    public int MediumPrizeAmount = 500;

    [DataField, AutoNetworkedField]
    public int BigPrizeAmount = 5000;

    [DataField, AutoNetworkedField]
    public int JackPotPrizeAmount = 20000;

    #endregion

    #region DoAfter

    /// <summary>
    /// How long the reels spin for. Should line up with the spin sound and animation.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float DoAfterTime = 3.8f;

    [DataField, AutoNetworkedField]
    public bool IsSpinning;

    #endregion
}

[Serializable, NetSerializable]
public enum SlotMachineVisuals : byte
{
    Spinning
}
