// SPDX-License-Identifier: AGPL-3.0-or-later
// Ported from Goobstation (there: CoinFliperComponent).

using Robust.Shared.Audio;
using Robust.Shared.GameStates;

namespace Content.Shared._Goobstation.SlotMachine;

/// <summary>
/// A double-or-nothing machine: activating it wagers the entire inserted cash stack on a coin
/// flip. Heads pays double, tails eats the money.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class CoinFlipperComponent : Component
{
    [DataField]
    public SoundSpecifier SpinSound = new SoundPathSpecifier("/Audio/_Goobstation/Machines/SlotMachine/slotmachine_spin.ogg");

    [DataField]
    public SoundSpecifier LoseSound = new SoundPathSpecifier("/Audio/Machines/buzz-two.ogg");

    [DataField]
    public SoundSpecifier WinSound = new SoundPathSpecifier("/Audio/Effects/Arcade/win.ogg");

    /// <summary>
    /// Chance for the flip to pay out.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float WinChance = 0.5f;

    [DataField, AutoNetworkedField]
    public float DoAfterTime = 3.8f;

    [DataField, AutoNetworkedField]
    public bool IsSpinning;

    /// <summary>
    /// The wagered amount while a flip is in progress.
    /// </summary>
    [DataField, AutoNetworkedField]
    public int PrizeAmount;
}
