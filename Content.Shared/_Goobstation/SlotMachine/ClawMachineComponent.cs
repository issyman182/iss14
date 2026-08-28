// SPDX-License-Identifier: AGPL-3.0-or-later
// Ported from Goobstation.

using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Goobstation.SlotMachine;

/// <summary>
/// A free-to-play claw machine: playing takes a moment of concentration (moving or taking damage
/// drops the plushie) and occasionally pays out a random reward. Emagging swaps the reward pool
/// for the evil one.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ClawMachineComponent : Component
{
    [DataField, AutoNetworkedField]
    public float DoAfterTime = 3.9f;

    [DataField]
    public SoundSpecifier PlaySound = new SoundPathSpecifier("/Audio/Machines/Keyboard/keyboard1.ogg");

    [DataField]
    public SoundSpecifier LoseSound = new SoundPathSpecifier("/Audio/Machines/buzz-two.ogg");

    [DataField]
    public SoundSpecifier WinSound = new SoundPathSpecifier("/Audio/Effects/Arcade/win.ogg");

    [DataField, AutoNetworkedField]
    public float WinChance = .10f;

    [DataField, AutoNetworkedField]
    public bool IsSpinning;

    [DataField, AutoNetworkedField]
    public List<EntProtoId>? Rewards;

    /// <summary>
    /// Replaces <see cref="Rewards"/> when the machine gets emagged.
    /// </summary>
    [DataField, AutoNetworkedField]
    public List<EntProtoId>? EvilRewards;
}

[Serializable, NetSerializable]
public enum ClawMachineVisuals : byte
{
    Spinning,
    NormalSprite
}
