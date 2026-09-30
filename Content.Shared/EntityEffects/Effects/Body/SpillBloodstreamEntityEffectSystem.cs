using Content.Shared.Body.Components;
using Content.Shared.Body.Systems;

namespace Content.Shared.EntityEffects.Effects.Body;

/// <summary>
/// Spills all bloodstream solutions from this entity.
/// </summary>
/// <inheritdoc cref="EntityEffectSystem{T, TEffect}"/>
public sealed partial class SpillBloodstreamEntityEffectSystem : EntityEffectSystem<BloodstreamComponent, SpillBloodstream>
{
    [Dependency] private SharedBloodstreamSystem _bloodstream = default!; // iss14: fork keeps its own bloodstream

    protected override void Effect(Entity<BloodstreamComponent> entity, ref EntityEffectEvent<SpillBloodstream> args)
    {
        _bloodstream.SpillAllSolutions(entity.AsNullable());
    }
}

/// <inheritdoc cref="EntityEffect"/>
public sealed partial class SpillBloodstream : EntityEffectBase<SpillBloodstream>;
