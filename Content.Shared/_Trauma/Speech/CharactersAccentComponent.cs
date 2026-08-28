// SPDX-License-Identifier: AGPL-3.0-or-later
// Ported from Trauma-Station, adapted to this fork's RelayAccentSystem idiom.

using Content.Shared.Speech.Components;
using Robust.Shared.GameStates;

namespace Content.Shared._Trauma.Speech;

/// <summary>
/// Replaces individual characters with a random pick from a per-character replacement list.
/// Used for the Swedish accent trait, among others.
/// </summary>
[RegisterComponent, NetworkedComponent]
[Access(typeof(CharactersAccentSystem))]
public sealed partial class CharactersAccentComponent : BaseAccentComponent
{
    [DataField(required: true)]
    public Dictionary<char, List<string>> Chars = new();
}
