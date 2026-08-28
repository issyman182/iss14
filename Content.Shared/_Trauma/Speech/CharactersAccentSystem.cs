// SPDX-License-Identifier: AGPL-3.0-or-later
// Ported from Trauma-Station, adapted to this fork's RelayAccentSystem idiom.

using System.Text;
using Content.Shared.Speech.EntitySystems;
using Robust.Shared.Random;

namespace Content.Shared._Trauma.Speech;

public sealed partial class CharactersAccentSystem : RelayAccentSystem<CharactersAccentComponent>
{
    [Dependency] private IRobustRandom _random = default!;

    private readonly StringBuilder _builder = new();

    public override string Accentuate(string message, Entity<CharactersAccentComponent>? ent = null)
    {
        if (ent is not { } entity)
            return message;

        _builder.Clear();
        var chars = entity.Comp.Chars;
        foreach (var c in message)
        {
            if (!chars.TryGetValue(c, out var replacements) || replacements.Count == 0)
            {
                _builder.Append(c);
                continue;
            }

            _builder.Append(_random.Pick(replacements));
        }

        return _builder.ToString();
    }
}
