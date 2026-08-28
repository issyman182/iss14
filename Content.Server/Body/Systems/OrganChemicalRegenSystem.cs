using Content.Shared._Shitmed.Medical.Surgery.Traumas.Systems;
using Content.Shared.Body.Organ;
using Content.Shared.EntityEffects.Effects;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs.Systems;

namespace Content.Server.Body.Systems;

/// <summary>
/// iss14: slowly heals the "ChemicalDamage" organ modifier applied by
/// <see cref="ChemicalOrganDamage"/> (drinking, smoking, drugs) while the body is alive.
/// A light bender heals back on their own; sustained abuse outpaces the regen and needs surgery.
/// </summary>
public sealed partial class OrganChemicalRegenSystem : EntitySystem
{
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private TraumaSystem _trauma = default!;

    /// <summary>
    /// How often the regeneration ticks, in seconds.
    /// </summary>
    private const float UpdateInterval = 2f;

    private float _accumulated;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _accumulated += frameTime;
        if (_accumulated < UpdateInterval)
            return;

        var interval = _accumulated;
        _accumulated = 0f;

        var query = EntityQueryEnumerator<ChemicallyDamagedOrganComponent, OrganComponent>();
        while (query.MoveNext(out var uid, out var regen, out var organ))
        {
            // Dead bodies (and loose organs) don't heal.
            if (organ.Body is not { } body || _mobState.IsDead(body))
                continue;

            if (!organ.IntegrityModifiers.TryGetValue(
                    (ChemicalOrganDamageEffectSystem.ModifierIdentifier, uid),
                    out var damage)
                || damage <= FixedPoint2.Zero)
            {
                _trauma.TryRemoveOrganDamageModifier(uid, uid, ChemicalOrganDamageEffectSystem.ModifierIdentifier, organ);
                RemCompDeferred<ChemicallyDamagedOrganComponent>(uid);
                continue;
            }

            var heal = FixedPoint2.Min(damage, regen.RegenPerSecond * interval);
            if (damage - heal <= FixedPoint2.Zero)
            {
                _trauma.TryRemoveOrganDamageModifier(uid, uid, ChemicalOrganDamageEffectSystem.ModifierIdentifier, organ);
                RemCompDeferred<ChemicallyDamagedOrganComponent>(uid);
            }
            else
            {
                _trauma.TryChangeOrganDamageModifier(uid, -heal, uid, ChemicalOrganDamageEffectSystem.ModifierIdentifier, organ);
            }
        }
    }
}
