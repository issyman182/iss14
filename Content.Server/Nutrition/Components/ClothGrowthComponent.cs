using Content.Server.Nutrition.EntitySystems;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Whitelist;

namespace Content.Server.Nutrition.Components;

/// <summary>
/// iss14: Makes a mob grow in size and melee damage as it eats whitelisted food,
/// e.g. mothroaches getting bigger and hitting harder the more cloth they eat.
/// </summary>
[RegisterComponent, Access(typeof(ClothGrowthSystem))]
public sealed partial class ClothGrowthComponent : Component
{
    /// <summary>
    /// Which food entities count towards growth.
    /// </summary>
    [DataField(required: true)]
    public EntityWhitelist Whitelist = new();

    /// <summary>
    /// Solution volume that counts as one unit of cloth.
    /// A single cloth sheet holds 10u of fiber, so the default makes one sheet = one unit.
    /// </summary>
    [DataField]
    public FixedPoint2 SolutionPerCloth = 10;

    /// <summary>
    /// Cloth units eaten so far.
    /// </summary>
    [DataField]
    public FixedPoint2 ClothEaten = FixedPoint2.Zero;

    /// <summary>
    /// Cloth units needed to reach full growth; eating more has no further effect.
    /// </summary>
    [DataField]
    public FixedPoint2 MaxCloth = 60;

    /// <summary>
    /// Sprite scale multiplier at full growth.
    /// </summary>
    [DataField]
    public float MaxScale = 2.2f;

    /// <summary>
    /// Melee damage added on top of the mob's base melee damage at full growth,
    /// scaling linearly with cloth eaten.
    /// </summary>
    [DataField(required: true)]
    public DamageSpecifier MaxDamageBonus = new();

    /// <summary>
    /// Whether physics fixtures grow along with the sprite.
    /// </summary>
    [DataField]
    public bool ScaleFixtures = true;

    /// <summary>
    /// The mob's melee damage before any growth was applied, captured on first growth.
    /// </summary>
    [DataField]
    public DamageSpecifier? BaseDamage;

    /// <summary>
    /// The growth scale currently applied, used to compute relative fixture scaling.
    /// </summary>
    [DataField]
    public float CurrentScale = 1f;
}
