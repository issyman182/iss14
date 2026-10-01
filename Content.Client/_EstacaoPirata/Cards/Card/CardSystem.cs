// iss14: EstacaoPirata playing cards (ported from Goob-Station). Direct SpriteComponent layer mutation replaced with SpriteSystem calls.
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Shared._EstacaoPirata.Cards.Card;
using Robust.Client.GameObjects;
using Robust.Shared.Utility;

namespace Content.Client._EstacaoPirata.Cards.Card;

/// <summary>
/// Swaps a card's sprite layers between its front and back when it is flipped.
/// </summary>
public sealed partial class CardSystem : EntitySystem
{
    [Dependency] private SpriteSystem _sprite = default!;

    /// <inheritdoc/>
    public override void Initialize()
    {
        SubscribeLocalEvent<CardComponent, ComponentStartup>(OnComponentStartupEvent);
        SubscribeNetworkEvent<CardFlipUpdatedEvent>(OnFlip);
    }

    private void OnComponentStartupEvent(EntityUid uid, CardComponent comp, ComponentStartup args)
    {
        if (!TryComp(uid, out SpriteComponent? sprite))
            return;

        var layerCount = sprite.AllLayers.Count();
        for (var i = 0; i < layerCount; i++)
        {
            if (!_sprite.TryGetLayer((uid, sprite), i, out var layer, false) || layer.State.Name == null)
                continue;

            var rsi = layer.RSI ?? sprite.BaseRSI;
            if (rsi == null)
                continue;

            comp.FrontSprite.Add(new SpriteSpecifier.Rsi(rsi.Path, layer.State.Name));
        }

        if (comp.BackSprite.Count == 0)
            comp.BackSprite = comp.FrontSprite;

        UpdateSprite(uid, comp);
    }

    private void OnFlip(CardFlipUpdatedEvent args)
    {
        var uid = GetEntity(args.Card);
        if (!TryComp(uid, out CardComponent? comp))
            return;
        UpdateSprite(uid, comp);
    }

    private void UpdateSprite(EntityUid uid, CardComponent comp)
    {
        var newSprite = comp.Flipped ? comp.BackSprite : comp.FrontSprite;

        if (!TryComp(uid, out SpriteComponent? sprite))
            return;

        var layerCount = newSprite.Count;
        var currentCount = sprite.AllLayers.Count();

        // Inserts missing layers
        if (currentCount < layerCount)
        {
            for (var i = currentCount; i < layerCount; i++)
            {
                _sprite.AddBlankLayer((uid, sprite), i);
            }
        }
        // Removes extra layers
        else if (currentCount > layerCount)
        {
            for (var i = currentCount - 1; i >= layerCount; i--)
            {
                _sprite.RemoveLayer((uid, sprite), i);
            }
        }

        for (var i = 0; i < layerCount; i++)
        {
            _sprite.LayerSetSprite((uid, sprite), i, newSprite[i]);
        }
    }
}
