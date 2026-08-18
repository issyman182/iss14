using Content.Shared.Damage.Components;
using Content.Shared.Mobs.Components;
using Robust.Shared.Timing;
using Content.Shared._Shitmed.Targeting; // Shitmed Change

namespace Content.Shared.Damage.Systems;

public sealed partial class PassiveDamageSystem : EntitySystem
{
    [Dependency] private DamageableSystem _damageable = default!;
    [Dependency] private IGameTiming _timing = default!;

    #region Subscriptions

    [SubscribeLocalEvent]
    private void OnPendingMapInit(Entity<PassiveDamageComponent> ent, ref MapInitEvent args)
    {
        ent.Comp.NextDamage = _timing.CurTime + TimeSpan.FromSeconds(1f);
        Dirty(ent);
    }

    [SubscribeLocalEvent]
    private void OnDamageTaken(Entity<PassiveDamageComponent> ent, ref DamageDealtEvent args)
    {
        if (ent.Comp.IntervalHaltOnDamageTaken == TimeSpan.Zero || !args.Damage.AnyPositive())
            return;

        var proposedUpdateTime = _timing.CurTime + ent.Comp.IntervalHaltOnDamageTaken;
        if (proposedUpdateTime > ent.Comp.NextDamage)
        {
            ent.Comp.NextDamage = proposedUpdateTime;
            Dirty(ent);
        }

    }

    #endregion

    // Every tick, attempt to damage entities
    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var curTime = _timing.CurTime;

        // Go through every entity with the component
        var query = EntityQueryEnumerator<PassiveDamageComponent, DamageableComponent>(); // Shitmed Change - no MobState requirement
        while (query.MoveNext(out var uid, out var comp, out var damage))
        {
            // Make sure they're up for a damage tick
            if (comp.NextDamage > curTime)
                continue;

            if (comp.DamageCap != 0 && _damageable.GetTotalDamage((uid, damage)) >= comp.DamageCap) // Goobstation - iss14: TotalDamage is no longer externally readable
                continue;

            // Set the next time they can take damage
            comp.NextDamage = curTime + TimeSpan.FromSeconds(1f);
            Dirty(uid, comp);

            // Goobstation
            if (comp.AllowedStates == null || !TryComp<MobStateComponent>(uid, out var mobState))
            {
                _damageable.TryChangeDamage(uid, comp.Damage, true, false, damage);
                continue; // iss14: Goob has `return;` here, which skips every other entity for the tick
            }

            // Damage them
            foreach (var allowedState in comp.AllowedStates)
            {
                if (allowedState == mobState.CurrentState)
                    _damageable.TryChangeDamage(uid, comp.Damage, true, false, damage, targetPart: TargetBodyPart.All, splitDamage: comp.SplitBehavior); // Shitmed Change
            }
        }
    }
}
