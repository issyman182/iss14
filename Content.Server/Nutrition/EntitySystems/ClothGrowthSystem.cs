using Content.Server.Nutrition.Components;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Nutrition;
using Content.Shared.Sprite;
using Content.Shared.Weapons.Melee;
using Content.Shared.Whitelist;
using Robust.Shared.Physics.Systems;

namespace Content.Server.Nutrition.EntitySystems;

/// <summary>
/// iss14: Grows mobs (sprite scale, fixtures and melee damage) as they eat whitelisted food.
/// See <see cref="ClothGrowthComponent"/>.
/// </summary>
public sealed partial class ClothGrowthSystem : EntitySystem
{
    [Dependency] private EntityWhitelistSystem _whitelist = default!;
    [Dependency] private SharedScaleVisualsSystem _scaleVisuals = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ClothGrowthComponent, IngestingEvent>(OnIngesting);
    }

    private void OnIngesting(Entity<ClothGrowthComponent> ent, ref IngestingEvent args)
    {
        var comp = ent.Comp;

        if (comp.ClothEaten >= comp.MaxCloth)
            return;

        if (_whitelist.IsWhitelistFail(comp.Whitelist, args.Food))
            return;

        comp.ClothEaten = FixedPoint2.Min(comp.ClothEaten + args.Split.Volume / comp.SolutionPerCloth, comp.MaxCloth);
        UpdateGrowth(ent);
    }

    private void UpdateGrowth(Entity<ClothGrowthComponent> ent)
    {
        var comp = ent.Comp;
        var fraction = (comp.ClothEaten / comp.MaxCloth).Float();

        if (TryComp<MeleeWeaponComponent>(ent, out var melee))
        {
            comp.BaseDamage ??= new DamageSpecifier(melee.Damage);
            melee.Damage = comp.BaseDamage + comp.MaxDamageBonus * fraction;
            Dirty(ent.Owner, melee);
        }

        var targetScale = 1f + (comp.MaxScale - 1f) * fraction;
        if (MathHelper.CloseTo(targetScale, comp.CurrentScale))
            return;

        var relative = targetScale / comp.CurrentScale;
        _scaleVisuals.SetSpriteScale(ent, _scaleVisuals.GetSpriteScale(ent) * relative);

        if (comp.ScaleFixtures)
            _physics.ScaleFixtures(ent.Owner, relative);

        comp.CurrentScale = targetScale;
    }
}
