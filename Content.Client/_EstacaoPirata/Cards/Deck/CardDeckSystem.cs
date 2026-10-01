// iss14: EstacaoPirata playing cards (ported from Goob-Station). Direct SpriteComponent layer mutation replaced with SpriteSystem calls.
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using Content.Shared._EstacaoPirata.Cards.Deck;
using Content.Shared._EstacaoPirata.Cards.Stack;
using Robust.Client.GameObjects;

namespace Content.Client._EstacaoPirata.Cards.Deck;

/// <summary>
/// Renders a deck as a pile of the top few cards it contains.
/// </summary>
public sealed partial class CardDeckSystem : EntitySystem
{
    private readonly Dictionary<Entity<CardDeckComponent>, int> _notInitialized = [];
    [Dependency] private CardSpriteSystem _cardSpriteSystem = default!;
    [Dependency] private SpriteSystem _sprite = default!;

    /// <inheritdoc/>
    public override void Initialize()
    {
        UpdatesOutsidePrediction = false;
        SubscribeLocalEvent<CardDeckComponent, ComponentStartup>(OnComponentStartupEvent);
        SubscribeNetworkEvent<CardStackInitiatedEvent>(OnStackStart);
        SubscribeNetworkEvent<CardStackQuantityChangeEvent>(OnStackUpdate);
        SubscribeNetworkEvent<CardStackReorderedEvent>(OnReorder);
        SubscribeNetworkEvent<CardStackFlippedEvent>(OnStackFlip);
        SubscribeLocalEvent<CardDeckComponent, AppearanceChangeEvent>(OnAppearanceChanged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Lazy way to make sure the sprite starts correctly
        foreach (var ent in _notInitialized.Keys.ToArray()) // iss14: copy, we mutate while iterating
        {
            var tries = _notInitialized[ent];
            if (tries >= 5)
            {
                _notInitialized.Remove(ent);
                continue;
            }

            _notInitialized[ent] = tries + 1;

            if (!TryComp(ent.Owner, out CardStackComponent? stack) || stack.Cards.Count <= 0)
                continue;

            // If the card was STILL not initialized, we skip it
            if (!TryGetCardLayer(stack.Cards.Last(), out _))
                continue;

            // If cards were correctly initialized, we update the sprite
            UpdateSprite(ent.Owner, ent.Comp);
            _notInitialized.Remove(ent);
        }
    }

    private bool TryGetCardLayer(EntityUid card, out SpriteComponent.Layer? layer)
    {
        layer = null;
        if (!TryComp(card, out SpriteComponent? cardSprite))
            return false;

        if (!_sprite.TryGetLayer((card, cardSprite), 0, out var l, false))
            return false;

        layer = l;
        return true;
    }

    private void UpdateSprite(EntityUid uid, CardDeckComponent comp)
    {
        if (!TryComp(uid, out SpriteComponent? sprite))
            return;

        if (!TryComp(uid, out CardStackComponent? cardStack))
            return;

        // Prevents error appearing at spawnMenu
        if (cardStack.Cards.Count <= 0 || !TryGetCardLayer(cardStack.Cards.Last(), out var cardlayer) ||
            cardlayer == null)
        {
            _notInitialized[(uid, comp)] = 0;
            return;
        }

        _cardSpriteSystem.TryAdjustLayerQuantity((uid, sprite, cardStack), comp.CardLimit);

        _cardSpriteSystem.TryHandleLayerConfiguration(
            (uid, sprite, cardStack),
            comp.CardLimit,
            (sprt, cardIndex, layerIndex) =>
            {
                _sprite.LayerSetRotation(sprt.AsNullable(), layerIndex, Angle.FromDegrees(90));
                _sprite.LayerSetOffset(sprt.AsNullable(), layerIndex, new Vector2(0, comp.YOffset * cardIndex));
                _sprite.LayerSetScale(sprt.AsNullable(), layerIndex, new Vector2(comp.Scale, comp.Scale));
                return true;
            }
        );
    }

    private void OnStackUpdate(CardStackQuantityChangeEvent args)
    {
        var uid = GetEntity(args.Stack);
        if (!TryComp(uid, out CardDeckComponent? comp))
            return;
        UpdateSprite(uid, comp);
    }

    private void OnStackFlip(CardStackFlippedEvent args)
    {
        var uid = GetEntity(args.CardStack);
        if (!TryComp(uid, out CardDeckComponent? comp))
            return;
        UpdateSprite(uid, comp);
    }

    private void OnReorder(CardStackReorderedEvent args)
    {
        var uid = GetEntity(args.Stack);
        if (!TryComp(uid, out CardDeckComponent? comp))
            return;
        UpdateSprite(uid, comp);
    }

    private void OnAppearanceChanged(EntityUid uid, CardDeckComponent comp, AppearanceChangeEvent args)
    {
        UpdateSprite(uid, comp);
    }

    private void OnComponentStartupEvent(EntityUid uid, CardDeckComponent comp, ComponentStartup args)
    {
        if (!TryComp(uid, out CardStackComponent? stack))
        {
            _notInitialized[(uid, comp)] = 0;
            return;
        }

        if (stack.Cards.Count <= 0)
            _notInitialized[(uid, comp)] = 0;
        UpdateSprite(uid, comp);
    }

    private void OnStackStart(CardStackInitiatedEvent args)
    {
        var entity = GetEntity(args.CardStack);
        if (!TryComp(entity, out CardDeckComponent? comp))
            return;

        UpdateSprite(entity, comp);
    }
}
