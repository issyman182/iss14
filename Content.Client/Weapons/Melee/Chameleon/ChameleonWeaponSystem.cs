// iss14: Cybersun MIMIC-5
using Content.Shared.Weapons.Melee.Chameleon;
using Robust.Client.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Client.Weapons.Melee.Chameleon;

/// <summary>
///     Client side of the chameleon weapon: re-derives the disguise visuals whenever the networked
///     <see cref="ChameleonWeaponComponent.Selected"/> id changes, so every client sees the same disguise.
/// </summary>
public sealed partial class ChameleonWeaponSystem : SharedChameleonWeaponSystem
{
    [Dependency] private SpriteSystem _sprite = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ChameleonWeaponComponent, AfterAutoHandleStateEvent>(HandleState);
    }

    private void HandleState(Entity<ChameleonWeaponComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        UpdateVisuals(ent.Owner, ent.Comp);
    }

    protected override void UpdateSprite(EntityUid uid, EntityPrototype proto)
    {
        base.UpdateSprite(uid, proto);

        if (TryComp(uid, out SpriteComponent? sprite)
            && proto.TryComp(out SpriteComponent? otherSprite, Factory))
        {
            // Same approach as ChameleonClothingSystem (which goes through the obsolete SpriteComponent.CopyFrom):
            // the source component comes from a prototype, so its Owner is EntityUid.Invalid rather than a real
            // entity. CopySprite only reads the component we hand it, so this works, but it is as fragile as the
            // clothing version if Resolve ever starts rejecting invalid owners.
            _sprite.CopySprite((EntityUid.Invalid, otherSprite), (uid, sprite));
        }
    }
}
