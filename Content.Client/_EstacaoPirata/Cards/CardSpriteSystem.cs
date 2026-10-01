// iss14: EstacaoPirata playing cards (ported from Goob-Station). Direct SpriteComponent layer mutation replaced with SpriteSystem calls.
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Shared._EstacaoPirata.Cards.Stack;
using Robust.Client.GameObjects;
using Robust.Shared.Utility;

namespace Content.Client._EstacaoPirata.Cards;

/// <summary>
/// Shared helpers for building a stack/hand sprite out of the sprites of the cards it contains.
/// </summary>
public sealed partial class CardSpriteSystem : EntitySystem
{
    [Dependency] private SpriteSystem _sprite = default!;

    /// <summary>
    /// Makes the stack sprite have exactly as many layers as the (visible) cards in the stack need.
    /// </summary>
    public bool TryAdjustLayerQuantity(Entity<SpriteComponent, CardStackComponent> uid, int? cardLimit = null)
    {
        var sprite = uid.Comp1;
        var stack = uid.Comp2;
        var cardCount = cardLimit == null ? stack.Cards.Count : Math.Min(stack.Cards.Count, cardLimit.Value);

        var layerCount = 0;
        // Gets the quantity of layers
        var relevantCards = stack.Cards.TakeLast(cardCount).ToList();
        foreach (var card in relevantCards)
        {
            if (!TryComp(card, out SpriteComponent? cardSprite))
                return false;

            layerCount += cardSprite.AllLayers.Count();
        }
        layerCount = int.Max(1, layerCount); // Frontier: you need one layer.

        var currentCount = sprite.AllLayers.Count();
        // Inserts missing layers
        if (currentCount < layerCount)
        {
            for (var i = currentCount; i < layerCount; i++)
            {
                _sprite.AddBlankLayer((uid.Owner, sprite), i);
            }
        }
        // Removes extra layers
        else if (currentCount > layerCount)
        {
            for (var i = currentCount - 1; i >= layerCount; i--)
            {
                _sprite.RemoveLayer((uid.Owner, sprite), i);
            }
        }

        return true;
    }

    /// <summary>
    /// Copies the sprite layers of the last <paramref name="cardCount"/> cards onto the stack sprite and lets
    /// <paramref name="layerFunc"/> (card index, layer index) position each layer.
    /// </summary>
    public bool TryHandleLayerConfiguration(Entity<SpriteComponent, CardStackComponent> uid, int cardCount, Func<Entity<SpriteComponent>, int, int, bool> layerFunc)
    {
        var sprite = uid.Comp1;
        var stack = uid.Comp2;

        // int = index of what card it is from
        List<(int, ISpriteLayer)> layers = [];

        var i = 0;
        var cards = stack.Cards.TakeLast(cardCount).ToList();
        foreach (var card in cards)
        {
            if (!TryComp(card, out SpriteComponent? cardSprite))
                return false;
            layers.AddRange(cardSprite.AllLayers.Select(layer => (i, layer)));
            i++;
        }

        var j = 0;
        foreach (var (cardIndex, layer) in layers)
        {
            _sprite.LayerSetVisible((uid.Owner, sprite), j, true);
            // iss14: set the RSI state from the card's own RSI rather than assuming the stack shares it
            if (layer.ActualRsi is { } rsi && layer.RsiState.Name is { } stateName)
                _sprite.LayerSetSprite((uid.Owner, sprite), j, new SpriteSpecifier.Rsi(rsi.Path, stateName));
            else
                _sprite.LayerSetTexture((uid.Owner, sprite), j, layer.Texture);
            layerFunc.Invoke((uid.Owner, sprite), cardIndex, j);
            j++;
        }

        return true;
    }
}
