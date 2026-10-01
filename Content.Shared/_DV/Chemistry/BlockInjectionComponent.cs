// SPDX-License-Identifier: AGPL-3.0-or-later
// Ported from Goob-Station (originally DeltaV): Chitinid species.

using Robust.Shared.GameStates;

namespace Content.Shared._DV.Chemistry;

/// <summary>
/// Prevents needle injectors (syringes) from injecting into this entity.
/// Hyposprays and other inject-on-use injectors are unaffected.
/// </summary>
/// <remarks>
/// iss14: Goob-Station ships this component without a system, so it did nothing there;
/// <see cref="BlockInjectionSystem"/> makes it work as the chitinid guidebook describes.
/// </remarks>
[RegisterComponent, NetworkedComponent]
public sealed partial class BlockInjectionComponent : Component;
