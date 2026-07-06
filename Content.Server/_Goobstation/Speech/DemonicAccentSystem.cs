// SPDX-FileCopyrightText: 2025 GoobBot <uristmchands@proton.me>
// SPDX-FileCopyrightText: 2025 Lumminal <81829924+Lumminal@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Goobstation.Common.Speech;
using Content.Server.Speech.EntitySystems;
using Content.Shared.Speech;
using Robust.Shared.Random;

namespace Content.Goobstation.Server.Speech;

public sealed partial class DemonicAccentSystem : EntitySystem // iss14: partial (RA0049)
{
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private ReplacementAccentSystem _replacement = default!;

    /// <inheritdoc/>
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<DemonicAccentComponent, AccentGetEvent>(OnAccentGet);
    }

    private void OnAccentGet(Entity<DemonicAccentComponent> entity, ref AccentGetEvent args)
    {
        var message = args.Message;

        message = _replacement.ApplyReplacements(message, "slaughterDemon"); // iss14: fixed accent prototype id (was "slaughter_demon", which does not exist)

        if (_random.Prob(0.15f))
        {
            var pick = _random.Next(1, 8);
            message = message + ' ' + Loc.GetString($"accent-demonic-suffix-{pick}");
        }

        message = message.ToUpperInvariant();

        args.Message = message;
    }
}
