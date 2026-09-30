using System.Linq;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Prototypes;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

// Shitmed Change
using Content.Shared._Shitmed.Body;
using Content.Shared._Shitmed.Body.Part;
using Content.Shared._Shitmed.Damage;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Components;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Body.Part;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Shared.Damage.Systems;

public sealed partial class DamageableSystem
{
    /// <returns>If the damage container can take the given damage type</returns>
    private bool SupportsType(ProtoId<DamageContainerPrototype>? container, ProtoId<DamageTypePrototype> type)
    {
        if (container is null)
            return true;

        return _supportedTypesByContainer[container.Value].Contains(type);
    }

    public DamageModifierSet? GetDamageModifierSet(Entity<DamageableComponent?> entity)
    {
        if (!_damageableQuery.Resolve(entity, ref entity.Comp, false)
            || entity.Comp.DamageModifierSetId is not { } proto
            || !ProtoMan.Resolve(proto, out var modifierSet)
           )
            return null;

        return modifierSet;
    }

    /// <summary>
    ///     Directly sets the damage in a damageable component.
    /// </summary>
    /// <remarks>
    ///     Useful for some unfriendly folk. Also ensures that cached values are updated and that a damage changed
    ///     event is raised.
    /// </remarks>
    public void SetDamage(Entity<DamageableComponent?> ent, DamageSpecifier damage)
    {
        if (!_damageableQuery.Resolve(ent, ref ent.Comp, false))
            return;

        foreach (var type in ent.Comp.Damage.DamageDict.Keys)
        {
            if (!damage.DamageDict.ContainsKey(type))
                ent.Comp.Damage.DamageDict.Remove(type);
        }

        foreach (var (type, amount) in damage.DamageDict)
        {
            ent.Comp.Damage.DamageDict[type] = amount;
        }

        OnEntityDamageChanged((ent, ent.Comp));
    }

    /// <summary>
    ///     Applies damage specified via a <see cref="DamageSpecifier"/>.
    /// </summary>
    /// <remarks>
    ///     <see cref="DamageSpecifier"/> is effectively just a dictionary of damage types and damage values. This
    ///     function just applies the container's resistances (unless otherwise specified) and then changes the
    ///     stored damage data. Division of group damage into types is managed by <see cref="DamageSpecifier"/>.
    /// </remarks>
    /// <returns>
    ///     If the attempt was successful or not.
    /// </returns>
    public bool TryChangeDamage(
        Entity<DamageableComponent?> ent,
        DamageSpecifier damage,
        bool ignoreResistances = false,
        bool interruptsDoAfters = true,
        EntityUid? origin = null,
        bool ignoreGlobalModifiers = false,
        TargetBodyPart? targetPart = null // Goobstation - ranged aim-miss falloff needs a part override
    )
    {
        //! Empty just checks if the DamageSpecifier is _literally_ empty, as in, is internal dictionary of damage types is empty.
        // If you deal 0.0 of some damage type, Empty will be false!
        return TryChangeDamage(ent, damage, out _, ignoreResistances, interruptsDoAfters, origin, ignoreGlobalModifiers, targetPart);
    }

    /// <summary>
    ///     Applies damage specified via a <see cref="DamageSpecifier"/>.
    /// </summary>
    /// <remarks>
    ///     <see cref="DamageSpecifier"/> is effectively just a dictionary of damage types and damage values. This
    ///     function just applies the container's resistances (unless otherwise specified) and then changes the
    ///     stored damage data. Division of group damage into types is managed by <see cref="DamageSpecifier"/>.
    /// </remarks>
    /// <returns>
    ///     If the attempt was successful or not.
    /// </returns>
    public bool TryChangeDamage(
        Entity<DamageableComponent?> ent,
        DamageSpecifier damage,
        out DamageSpecifier newDamage,
        bool ignoreResistances = false,
        bool interruptsDoAfters = true,
        EntityUid? origin = null,
        bool ignoreGlobalModifiers = false,
        TargetBodyPart? targetPart = null // Goobstation - ranged aim-miss falloff needs a part override
    )
    {
        //! Empty just checks if the DamageSpecifier is _literally_ empty, as in, is internal dictionary of damage types is empty.
        // If you deal 0.0 of some damage type, Empty will be false!
        newDamage = ChangeDamage(ent, damage, ignoreResistances, interruptsDoAfters, origin, ignoreGlobalModifiers, targetPart: targetPart);
        return !newDamage.Empty;
    }

    /// <summary>
    ///     Applies damage specified via a <see cref="DamageSpecifier"/>.
    /// </summary>
    /// <remarks>
    ///     <see cref="DamageSpecifier"/> is effectively just a dictionary of damage types and damage values. This
    ///     function just applies the container's resistances (unless otherwise specified) and then changes the
    ///     stored damage data. Division of group damage into types is managed by <see cref="DamageSpecifier"/>.
    /// </remarks>
    /// <returns>
    ///     The actual amount of damage dealt, as a DamageSpecifier.
    /// </returns>
    public DamageSpecifier ChangeDamage(
        Entity<DamageableComponent?> ent,
        DamageSpecifier damage,
        bool ignoreResistances = false,
        bool interruptsDoAfters = true,
        EntityUid? origin = null,
        bool ignoreGlobalModifiers = false,
        // iss14: exposed so aggregate-damage callers (HealEvenly/HealDistributed, chem effects)
        // can split across all organic parts instead of hitting one random vital part.
        TargetBodyPart? targetPart = null,
        SplitDamageBehavior splitDamage = SplitDamageBehavior.Split,
        bool ignoreBlockers = false
    )
    {
        // Shitmed Change Start - route everything through the body-aware damage path so damage
        // on entities with body parts is applied to (and summed from) their parts/wounds.
        return TryChangeDamage(ent.Owner,
            damage,
            ignoreResistances,
            interruptsDoAfters,
            ent.Comp,
            origin,
            targetPart: targetPart,
            splitDamage: splitDamage,
            ignoreBlockers: ignoreBlockers,
            ignoreGlobalModifiers: ignoreGlobalModifiers) ?? new DamageSpecifier();
        // Shitmed Change End
    }

    // Shitmed Change Start
    /// <summary>
    ///     If the damage in a DamageableComponent was changed, this function should be called.
    ///     Goob-compatible public wrapper around upstream's OnEntityDamageChanged.
    /// </summary>
    public void DamageChanged(EntityUid uid,
        DamageableComponent component,
        DamageSpecifier? damageDelta = null,
        bool interruptsDoAfters = true,
        EntityUid? origin = null,
        bool ignoreBlockers = false,
        DamageSpecifier? uncappedDamage = null) // Goobstation
    {
        OnEntityDamageChanged((uid, component), damageDelta, interruptsDoAfters, origin, ignoreBlockers, uncappedDamage);
    }

    /// <summary>
    ///     Applies damage specified via a <see cref="DamageSpecifier"/>, routing it through body
    ///     parts and wounds where applicable.
    /// </summary>
    /// <remarks>
    ///     <see cref="DamageSpecifier"/> is effectively just a dictionary of damage types and damage values. This
    ///     function just applies the container's resistances (unless otherwise specified) and then changes the
    ///     stored damage data. Division of group damage into types is managed by <see cref="DamageSpecifier"/>.
    /// </remarks>
    /// <returns>
    ///     Returns a <see cref="DamageSpecifier"/> with information about the actual damage changes. This will be
    ///     null if the user had no applicable components that can take damage.
    /// </returns>
    public DamageSpecifier? TryChangeDamage(EntityUid? uid,
        DamageSpecifier damage,
        bool ignoreResistances = false,
        bool interruptsDoAfters = true,
        DamageableComponent? damageable = null,
        EntityUid? origin = null,
        bool canBeCancelled = false,
        float partMultiplier = 1.00f,
        TargetBodyPart? targetPart = null,
        bool ignoreBlockers = false,
        SplitDamageBehavior splitDamage = SplitDamageBehavior.Split,
        bool canMiss = true,
        bool ignoreGlobalModifiers = false) // iss14: threaded through from upstream's ChangeDamage
    {
        if (!uid.HasValue || !_damageableQuery.Resolve(uid.Value, ref damageable, false))
            return null;

        if (damage.Empty)
            return damage;

        // Goobstation start
        var vitalDamage = new DamageSpecifier(damage);
        vitalDamage -= vitalDamage;
        vitalDamage.TrimZeros();
        foreach (var type in _vitalOnlyDamageTypes)
        {
            // iss14: TryIndex instead of Index so missing (Goob-only) damage groups don't throw.
            if (ProtoMan.TryIndex(type, out var groupProto))
                vitalDamage += new DamageSpecifier(groupProto, FixedPoint2.Zero);
        }
        vitalDamage.ExclusiveAdd(damage);
        vitalDamage.TrimZeros();
        // Goobstation end

        var before = new BeforeDamageChangedEvent(damage, origin, canBeCancelled, targetPart); // Shitmed Change
        RaiseLocalEvent(uid.Value, ref before);

        if (before.Cancelled)
            return null;

        // For entities with a body, route damage through body parts and then sum it up
        if (_bodyQuery.TryGetComponent(uid.Value, out var body)
            && body.BodyType == BodyType.Complex)
        {
            damage -= vitalDamage; // Goobstation
            damage.TrimZeros(); // Goobstation

            var appliedDamage = ApplyDamageToBodyParts(uid.Value, damage, origin, ignoreResistances,
                interruptsDoAfters, targetPart, partMultiplier, ignoreBlockers, splitDamage, canMiss, ignoreGlobalModifiers);

            // Goobstation start
            var appliedVitalDamage = ApplyDamageToBodyParts(uid.Value, vitalDamage, origin, ignoreResistances,
                interruptsDoAfters, TargetBodyPart.Vital, partMultiplier, ignoreBlockers, splitDamage, canMiss, ignoreGlobalModifiers);

            var totalDamage = appliedDamage;
            if (totalDamage != null && appliedVitalDamage != null)
                totalDamage += appliedVitalDamage;

            return totalDamage;
            // Goobstation end
        }

        // For entities without a body, apply damage directly
        return ApplyDamageToEntity(uid.Value, damage, ignoreResistances, interruptsDoAfters, origin, damageable, ignoreBlockers, ignoreGlobalModifiers);
    }

    /// <summary>
    /// Applies damage to an entity with body parts, targeting specific parts as needed.
    /// </summary>
    private DamageSpecifier? ApplyDamageToBodyParts(
        EntityUid uid,
        DamageSpecifier damage,
        EntityUid? origin,
        bool ignoreResistances,
        bool interruptsDoAfters,
        TargetBodyPart? targetPart,
        float partMultiplier,
        bool ignoreBlockers = false,
        SplitDamageBehavior splitDamageBehavior = SplitDamageBehavior.Split,
        bool canMiss = true,
        bool ignoreGlobalModifiers = false)
    {
        DamageSpecifier? totalAppliedDamage = null;
        var adjustedDamage = damage * partMultiplier;
        // This cursed shitcode lets us know if the target part is a power of 2
        // therefore having multiple parts targeted.
        if (targetPart != null
            && targetPart != 0 && (targetPart & (targetPart - 1)) != 0)
        {
            // Extract only the body parts that are targeted in the bitmask
            var targetedBodyParts = new List<(EntityUid Id,
                BodyPartComponent Component,
                DamageableComponent Damageable)>();

            // Get only the primitive flags (powers of 2) - these are the actual individual body parts
            var primitiveFlags = Enum.GetValues<TargetBodyPart>()
                .Where(flag => flag != 0 && (flag & (flag - 1)) == 0) // Power of 2 check
                .ToList();

            foreach (var flag in primitiveFlags)
            {
                // Check if this specific flag is set in our targetPart bitmask
                if (targetPart.Value.HasFlag(flag))
                {
                    var query = _body.ConvertTargetBodyPart(flag);
                    var parts = _body.GetBodyChildrenOfTypeWithComponent<DamageableComponent>(uid, query.Type,
                        symmetry: query.Symmetry).ToList();

                    if (parts.Count > 0)
                        targetedBodyParts.AddRange(parts);
                }
            }

            // If we couldn't find any of the targeted parts, fall back to all body parts
            if (targetedBodyParts.Count == 0)
            {
                var query = _body.GetBodyChildrenWithComponent<DamageableComponent>(uid).ToList();
                if (query.Count > 0)
                    targetedBodyParts = query;
                else
                    return null;
            }

            // Goob edit start
            List<float>? multipliers = null;
            var damagePerPart = adjustedDamage;
            if (targetedBodyParts.Count > 0 && adjustedDamage.PartDamageVariation != 0f)
            {
                multipliers =
                    GetDamageVariationMultipliers(adjustedDamage.PartDamageVariation, targetedBodyParts.Count);
            }
            else
                damagePerPart = ApplySplitDamageBehaviors(splitDamageBehavior, adjustedDamage, targetedBodyParts);
            var appliedDamage = new DamageSpecifier();
            var surplusHealing = new DamageSpecifier();
            for (var i = 0; i < targetedBodyParts.Count; i++)
            {
                var (partId, _, partDamageable) = targetedBodyParts[i];
                var modifiedDamage = damagePerPart;
                if (multipliers != null && multipliers.Count == targetedBodyParts.Count)
                    modifiedDamage *= multipliers[i];
                modifiedDamage += surplusHealing;
                // Goob edit end

                // Apply damage to this part
                var partDamageResult = TryChangeDamage(partId, modifiedDamage, ignoreResistances,
                    interruptsDoAfters, partDamageable, origin, ignoreBlockers: ignoreBlockers, ignoreGlobalModifiers: ignoreGlobalModifiers);

                if (partDamageResult != null && !partDamageResult.Empty)
                {
                    appliedDamage += partDamageResult;

                    /*
                        Why this ugly shitcode? Its so that we can track chems and other sorts of healing surpluses.
                        Assume you're fighting in a spaced area. Your chest has 30 damage, and every other part
                        is getting 0.5 per tick. Your chems will only be 1/11th as effective, so we take the surplus
                        healing and pass it along parts. That way a chem that would heal you for 75 brute would truly
                        heal the 75 brute per tick, and not some weird shit like 6.8 per tick.
                    */
                    foreach (var (type, damageFromDict) in modifiedDamage.DamageDict)
                    {
                        if (damageFromDict >= 0
                            || !partDamageResult.DamageDict.TryGetValue(type, out var damageFromResult)
                            || damageFromResult > 0)
                            continue;

                        // If the damage from the dict plus the surplus healing is equal to the damage from the result,
                        // we can safely set the surplus healing to 0, as that means we consumed all of it.
                        if (damageFromDict >= damageFromResult)
                        {
                            surplusHealing.DamageDict[type] = FixedPoint2.Zero;
                        }
                        else
                        {
                            if (surplusHealing.DamageDict.TryGetValue(type, out var _))
                                surplusHealing.DamageDict[type] = damageFromDict - damageFromResult;
                            else
                                surplusHealing.DamageDict.TryAdd(type, damageFromDict - damageFromResult);
                        }
                    }
                }
            }

            totalAppliedDamage = appliedDamage;
        }
        else
        {
            // Target a specific body part
            TargetBodyPart? target;
            var totalDamage = damage.GetTotal();

            if (totalDamage <= 0 || !canMiss) // Whoops i think i fucked up damage here.
                target = _body.GetTargetBodyPart(uid, origin, targetPart);
            else
                target = _body.GetRandomBodyPart(uid, origin, targetPart);

            var (partType, symmetry) = _body.ConvertTargetBodyPart(target);
            var possibleTargets = _body.GetBodyChildrenOfType(uid, partType, symmetry: symmetry).ToList();

            if (possibleTargets.Count == 0)
            {
                if (totalDamage <= 0)
                    return null;

                possibleTargets = _body.GetBodyChildren(uid).ToList();
            }

            // No body parts at all?
            if (possibleTargets.Count == 0)
                return null;

            var chosenTarget = _random.PickAndTake(possibleTargets);

            if (!_damageableQuery.TryComp(chosenTarget.Id, out var partDamageable))
                return null;

            totalAppliedDamage = TryChangeDamage(chosenTarget.Id, adjustedDamage, ignoreResistances,
                interruptsDoAfters, partDamageable, origin, ignoreBlockers: ignoreBlockers, ignoreGlobalModifiers: ignoreGlobalModifiers);
        }

        return totalAppliedDamage;
    }

    /// <summary>
    /// Applies damage directly to an entity without routing through body parts.
    /// </summary>
    private DamageSpecifier? ApplyDamageToEntity(
        EntityUid uid,
        DamageSpecifier? damage,
        bool ignoreResistances,
        bool interruptsDoAfters,
        EntityUid? origin,
        DamageableComponent? damageable = null,
        bool ignoreBlockers = false,
        bool ignoreGlobalModifiers = false)
    {
        if (!Resolve(uid, ref damageable) || damage == null)
            return null;

        // Apply resistances
        if (!ignoreResistances)
        {
            if (GetDamageModifierSet((uid, damageable)) is { } modifierSet)
            {
                damage = DamageSpecifier.ApplyModifierSet(damage,
                    DamageSpecifier.PenetrateArmor(modifierSet, damage.ArmorPenetration)); // Goob edit
            }

            if (TryComp(uid, out BodyPartComponent? bodyPart))
            {
                TargetBodyPart? target = _body.GetTargetBodyPart(bodyPart);
                if (bodyPart.Body != null)
                {
                    // First raise the event on the parent to apply any parent modifiers
                    var parentEv = new DamageModifyEvent(bodyPart.Body.Value, damage, origin, target);
                    RaiseLocalEvent(bodyPart.Body.Value, parentEv);
                    damage = parentEv.Damage;
                }

                // Then raise on the part itself for any part-specific modifiers
                var ev = new DamageModifyEvent(uid, damage, origin, target);
                RaiseLocalEvent(uid, ev);
                damage = ev.Damage;
            }
            else
            {
                // Not a body part, just apply modifiers normally
                var ev = new DamageModifyEvent(uid, damage, origin);
                RaiseLocalEvent(uid, ev);
                damage = ev.Damage;
            }

            if (damage.Empty)
                return damage;
        }

        if (!ignoreGlobalModifiers) // iss14: upstream flag, kept working
            damage = ApplyUniversalAllModifiers(damage);

        var delta = new DamageSpecifier(damage.ArmorPenetration,
            damage.PartDamageVariation,
            damage.WoundSeverityMultipliers); // Goob edit
        delta.DamageDict.EnsureCapacity(damage.DamageDict.Count);
        var dict = damageable.Damage.DamageDict;

        // Check for integrity cap on body parts
        bool isWoundable = false;
        FixedPoint2? damageCap = null;
        if (_woundableQuery.TryComp(uid, out var woundable))
        {
            isWoundable = true;
            damageCap = woundable.IntegrityCap;
        }

        // iss14: upstream keeps damage dicts sparse and filters supported damage types via
        // InjurableComponent's damage container, so consult it for types not yet in the dict.
        _injurableQuery.TryComp(uid, out var injurable);

        // Apply damage
        var currentTotalDamage = damageable.TotalDamage.Float();
        FixedPoint2? remainingCap = damageCap.HasValue ? damageCap.Value - currentTotalDamage : null;

        foreach (var (type, value) in damage.DamageDict)
        {
            if (!dict.TryGetValue(type, out var oldValue))
            {
                // iss14: sparse dict - only allow types the entity's damage container supports.
                if (injurable == null || !SupportsType(injurable.DamageContainer, type))
                    continue;

                oldValue = FixedPoint2.Zero;
            }

            // For positive damage, we need to check if we've hit the cap
            if (value > 0)
            {
                // Delta ignores this stuff since we need it for effects.
                delta.DamageDict[type] = value;

                // If we're not a woundable or we don't have a cap, apply the damage normally
                if (!isWoundable
                    || remainingCap is null)
                {
                    dict[type] = oldValue + value;
                    continue;
                }

                // If we've already hit the cap, skip this damage type
                if (remainingCap.Value <= 0)
                    continue;

                // Calculate how much of this damage type we can apply
                var damageToApply = FixedPoint2.Min(value, remainingCap.Value);
                var newValue = FixedPoint2.Max(FixedPoint2.Zero, oldValue + damageToApply);

                // Update remaining cap
                remainingCap -= damageToApply;

                // Only update the dict if the value actually changed
                if (newValue != oldValue)
                    dict[type] = newValue;
            }
            else
            {
                // For negative damage (healing), apply normally
                var newValue = FixedPoint2.Max(FixedPoint2.Zero, oldValue + value);
                if (newValue != oldValue)
                {
                    dict[type] = newValue;
                    delta.DamageDict[type] = newValue - oldValue;
                }
            }
        }

        // Goob edit start
        OnEntityDamageChanged((uid, damageable), delta, interruptsDoAfters, origin, ignoreBlockers, damage);

        // iss14: keep upstream's DamageDealtEvent as a notification of the applied delta.
        var evt = new DamageDealtEvent(delta, origin, interruptsDoAfters);
        RaiseLocalEvent(uid, ref evt);

        // Shitmed Change: This means that the damaged part was a woundable
        // which also means we send that shit to refresh the body.
        if (delta.DamageDict.Count > 0 && isWoundable)
        {
            UpdateParentDamageFromBodyParts(uid,
                delta,
                interruptsDoAfters,
                origin,
                ignoreBlockers: ignoreBlockers);
        }
        // Goob edit end

        return delta;
    }

    /// <summary>
    /// Updates the parent entity's damage values by summing damage from all body parts.
    /// Should be called after damage is applied to any body part.
    /// </summary>
    /// <param name="bodyPartUid">The body part that received damage</param>
    /// <param name="appliedDamage">The damage that was applied to the body part</param>
    /// <param name="interruptsDoAfters">Whether this damage change interrupts do-afters</param>
    /// <param name="origin">The entity that caused the damage</param>
    /// <param name="ignoreBlockers">Whether to ignore damage blockers</param>
    /// <returns>True if parent damage was updated, false otherwise</returns>
    private bool UpdateParentDamageFromBodyParts(
        EntityUid bodyPartUid,
        DamageSpecifier? appliedDamage,
        bool interruptsDoAfters,
        EntityUid? origin,
        BodyPartComponent? bodyPart = null,
        bool ignoreBlockers = false)
    {
        // Check if this is a body part and get the parent body
        if (!Resolve(bodyPartUid, ref bodyPart, logMissing: false)
            || bodyPart.Body is not { } body
            || !TryComp(body, out DamageableComponent? parentDamageable))
            return false;

        // Reset the parent's damage values
        foreach (var type in parentDamageable.Damage.DamageDict.Keys.ToList())
            parentDamageable.Damage.DamageDict[type] = FixedPoint2.Zero;

        // Sum up damage from all body parts
        foreach (var (partId, _) in _body.GetBodyChildren(body))
        {
            if (!_damageableQuery.TryComp(partId, out var partDamageable))
                continue;

            foreach (var (type, value) in partDamageable.Damage.DamageDict)
            {
                if (value == 0)
                    continue;

                if (parentDamageable.Damage.DamageDict.TryGetValue(type, out var existing))
                    parentDamageable.Damage.DamageDict[type] = existing + value;
                else
                    parentDamageable.Damage.DamageDict[type] = value; // iss14: sparse dicts upstream, add missing types
            }
        }

        // Raise the damage changed event on the parent
        OnEntityDamageChanged((body, parentDamageable),
            appliedDamage,
            interruptsDoAfters,
            origin,
            ignoreBlockers);

        return true;
    }

    public List<float> GetDamageVariationMultipliers(float variation, int count)
    {
        DebugTools.AssertNotEqual(count, 0);
        var list = new List<float>(count);
        var weights = new List<float>(count);
        var totalWeight = 0f;
        var random = new System.Random((int) _timing.CurTick.Value);
        for (var i = 0; i < count; i++)
        {
            var weight = random.NextFloat() * MathF.Abs(variation) + 1f;
            weights.Add(weight);
            totalWeight += weight;
        }

        DebugTools.AssertNotEqual(totalWeight, 0f);

        foreach (var weight in weights)
        {
            list.Add(weight / totalWeight);
        }

        return list;
    }

    public DamageSpecifier ApplySplitDamageBehaviors(SplitDamageBehavior splitDamageBehavior,
        DamageSpecifier damage,
        List<(EntityUid Id, BodyPartComponent Component, DamageableComponent Damageable)> parts)
    {
        var newDamage = new DamageSpecifier(damage);
        switch (splitDamageBehavior)
        {
            case SplitDamageBehavior.None:
                return newDamage;
            case SplitDamageBehavior.Split:
                return newDamage / parts.Count;
            case SplitDamageBehavior.SplitEnsureAllDamaged:
                var damagedParts = parts.Where(part =>
                    part.Damageable.TotalDamage > FixedPoint2.Zero).ToList();

                parts.Clear();
                parts.AddRange(damagedParts);

                goto case SplitDamageBehavior.SplitEnsureAll;
            case SplitDamageBehavior.SplitEnsureAllOrganic:
                // iss14: prefer organic parts, but fall back to all parts for fully inorganic
                // bodies (skeletons) - otherwise chem and reactive heals are zeroed entirely
                // (e.g. splashing milk on a skeleton healed nothing).
                var organicParts = parts.Where(part =>
                    part.Component.PartComposition == BodyPartComposition.Organic).ToList();

                if (organicParts.Count > 0)
                {
                    parts.Clear();
                    parts.AddRange(organicParts);
                }

                goto case SplitDamageBehavior.SplitEnsureAll;
            case SplitDamageBehavior.SplitEnsureAllDamagedAndOrganic:
                // iss14: same fallback as above; damaged parts are still required.
                var compatableParts = parts.Where(part =>
                    part.Damageable.TotalDamage > FixedPoint2.Zero &&
                    part.Component.PartComposition == BodyPartComposition.Organic).ToList();

                if (compatableParts.Count == 0)
                {
                    compatableParts = parts.Where(part =>
                        part.Damageable.TotalDamage > FixedPoint2.Zero).ToList();
                }

                parts.Clear();
                parts.AddRange(compatableParts);
                goto case SplitDamageBehavior.SplitEnsureAll;
            case SplitDamageBehavior.SplitEnsureAll:
                foreach (var (type, val) in newDamage.DamageDict)
                {
                    if (val > 0)
                    {
                        if (parts.Count > 0)
                            newDamage.DamageDict[type] = val / parts.Count;
                        else
                            newDamage.DamageDict[type] = FixedPoint2.Zero;
                    }
                    else if (val < 0)
                    {
                        var count = 0;

                        foreach (var (id, _, damageable) in parts)
                            if (damageable.Damage.DamageDict.TryGetValue(type, out var currentDamage)
                                && currentDamage > 0)
                                count++;

                        if (count > 0)
                            newDamage.DamageDict[type] = val / count;
                        else
                            newDamage.DamageDict[type] = FixedPoint2.Zero;
                    }
                }
                // We sort the parts to ensure that surplus damage gets passed from least to most damaged.
                parts.Sort((a, b) => a.Damageable.TotalDamage.CompareTo(b.Damageable.TotalDamage));
                return newDamage;
            default:
                return damage;
        }
    }

    public Dictionary<string, FixedPoint2> DamageSpecifierToWoundList(
        EntityUid uid,
        EntityUid? origin,
        TargetBodyPart targetPart,
        DamageSpecifier damageSpecifier,
        DamageableComponent damageable,
        bool ignoreResistances = false,
        float partMultiplier = 1.00f)
    {
        var damageDict = new Dictionary<string, FixedPoint2>();

        damageSpecifier = ApplyUniversalAllModifiers(damageSpecifier);

        // some wounds like Asphyxiation and Bloodloss aren't supposed to be created.
        if (!ignoreResistances)
        {
            if (GetDamageModifierSet((uid, damageable)) is { } modifierSet)
            {
                // lol bozo
                var spec = new DamageSpecifier
                {
                    DamageDict = damageSpecifier.DamageDict,
                };

                damageSpecifier = DamageSpecifier.ApplyModifierSet(spec, modifierSet);
            }

            var ev = new DamageModifyEvent(uid, damageSpecifier, origin, targetPart);
            RaiseLocalEvent(uid, ev);
            damageSpecifier = ev.Damage;

            if (damageSpecifier.Empty)
            {
                return damageDict;
            }
        }

        foreach (var (type, severity) in damageSpecifier.DamageDict)
        {
            if (!ProtoMan.TryIndex<EntityPrototype>(type, out var woundPrototype)
                || !woundPrototype.TryGetComponent<WoundComponent>(out _, _factory)
                || severity <= 0)
                continue;

            damageDict.Add(type, severity * partMultiplier);
        }

        return damageDict;
    }

    /// <summary>
    ///     Change the DamageContainer of a DamageableComponent. - Goobstation, Rubin Code
    /// </summary>
    public void ChangeDamageContainer(EntityUid uid, string newDamageContainerId, DamageableComponent? component = null)
    {
        if (!Resolve(uid, ref component, logMissing: false)
            || newDamageContainerId == component.DamageContainerID)
        {
            return;
        }

        // Try to get the new DamageContainerPrototype
        if (!ProtoMan.TryIndex<DamageContainerPrototype>(newDamageContainerId, out var damageContainerPrototype))
        {
            // Return early if no DamageContainerPrototype is found
            return;
        }

        // Update the DamageContainerID
        component.DamageContainerID = new ProtoId<DamageContainerPrototype>(newDamageContainerId);

        // Clear the existing damage dictionary
        component.Damage.DamageDict.Clear();

        // Initialize damage dictionary, using the types and groups from the damage container prototype
        foreach (var type in damageContainerPrototype.SupportedTypes)
        {
            component.Damage.DamageDict.TryAdd(type, FixedPoint2.Zero);
        }

        foreach (var groupId in damageContainerPrototype.SupportedGroups)
        {
            var group = ProtoMan.Index<DamageGroupPrototype>(groupId);
            foreach (var type in group.DamageTypes)
            {
                component.Damage.DamageDict.TryAdd(type, FixedPoint2.Zero);
            }
        }

        component.Damage.GetDamagePerGroup(ProtoMan, component.DamagePerGroup);
        component.TotalDamage = component.Damage.GetTotal();
    }
    // Shitmed Change End

    /// <summary>
    /// Will reduce the damage on the entity exactly by <see cref="amount"/> as close as equally distributed among all damage types the entity has.
    /// If one of the damage types of the entity is too low. it will heal that completly and distribute the excess healing among the other damage types.
    /// If the <see cref="amount"/> is larger than the total damage of the entity then it just clears all damage.
    /// </summary>
    /// <param name="ent">entity to be healed</param>
    /// <param name="amount">how much to heal. value has to be negative to heal</param>
    /// <param name="group">from which group to heal. if null, heal from all groups</param>
    /// <param name="origin">who did the healing</param>
    public DamageSpecifier HealEvenly(
        Entity<DamageableComponent?> ent,
        FixedPoint2 amount,
        ProtoId<DamageGroupPrototype>? group = null,
        EntityUid? origin = null)
    {
        var damageChange = new DamageSpecifier();

        if (!_damageableQuery.Resolve(ent, ref ent.Comp, false) || amount >= 0)
            return damageChange;

        // Get our total damage, or heal if we're below a certain amount.
        if (!TryGetDamageGreaterThan((ent, ent.Comp), -amount, out var damage, group))
            return ChangeDamage(ent, -damage, true, false, origin, targetPart: TargetBodyPart.All, splitDamage: SplitDamageBehavior.SplitEnsureAllOrganic, ignoreBlockers: true); // iss14: split heals across organic parts (Goob semantics)

        // make sure damageChange has the same damage types as damage
        damageChange.DamageDict.EnsureCapacity(damage.DamageDict.Count);
        foreach (var type in damage.DamageDict.Keys)
        {
            damageChange.DamageDict.Add(type, FixedPoint2.Zero);
        }

        var remaining = -amount;
        var keys = damage.DamageDict.Keys.ToList();

        while (remaining > 0)
        {
            var count = keys.Count;
            // We do this to ensure that we always round up when dividing to avoid excess loops.
            // We already have logic to prevent healing more than we have.
            var maxHeal = count == 1 ? remaining : (remaining + FixedPoint2.Epsilon * (count - 1)) / count;

            // Iterate backwards since we're removing items.
            for (var i = count - 1; i >= 0; i--)
            {
                var type = keys[i];
                // This is the amount we're trying to heal, capped by maxHeal
                var heal = damage.DamageDict[type] + damageChange.DamageDict[type];

                // Don't go above max, if we don't go above max
                if (heal > maxHeal)
                    heal = maxHeal;
                // If we're not above max, we will heal it fully and don't need to enumerate anymore!
                else
                    keys.RemoveAt(i);

                if (heal >= remaining)
                {
                    // Don't remove more than we can remove. Prevents us from healing more than we'd expect...
                    damageChange.DamageDict[type] -= remaining;
                    remaining = FixedPoint2.Zero;
                    break;
                }

                remaining -= heal;
                damageChange.DamageDict[type] -= heal;
            }
        }

        return ChangeDamage(ent, damageChange, true, false, origin, targetPart: TargetBodyPart.All, splitDamage: SplitDamageBehavior.SplitEnsureAllOrganic, ignoreBlockers: true); // iss14: split heals across organic parts (Goob semantics)
    }

    /// <summary>
    /// Will reduce the damage on the entity exactly by <see cref="amount"/> distributed by weight among all damage types the entity has.
    /// (the weight is how much damage of the type there is)
    /// If the <see cref="amount"/> is larger than the total damage of the entity then it just clears all damage.
    /// </summary>
    /// <param name="ent">entity to be healed</param>
    /// <param name="amount">how much to heal. value has to be negative to heal</param>
    /// <param name="group">from which group to heal. if null, heal from all groups</param>
    /// <param name="origin">who did the healing</param>
    public DamageSpecifier HealDistributed(
        Entity<DamageableComponent?> ent,
        FixedPoint2 amount,
        ProtoId<DamageGroupPrototype>? group = null,
        EntityUid? origin = null)
    {
        var damageChange = new DamageSpecifier();

        if (!_damageableQuery.Resolve(ent, ref ent.Comp, false) || amount >= 0)
            return damageChange;

        // Get our total damage, or heal if we're below a certain amount.
        if (!TryGetDamageGreaterThan((ent, ent.Comp), -amount, out var damage, group))
            return ChangeDamage(ent, -damage, true, false, origin, targetPart: TargetBodyPart.All, splitDamage: SplitDamageBehavior.SplitEnsureAllOrganic, ignoreBlockers: true); // iss14: split heals across organic parts (Goob semantics)

        // make sure damageChange has the same damage types as damageEntity
        damageChange.DamageDict.EnsureCapacity(damage.DamageDict.Count);
        var total = damage.GetTotal();

        // heal weighted by the damage of that type
        foreach (var (type, value) in damage.DamageDict)
        {
            damageChange.DamageDict.Add(type, value / total * amount);
        }

        return ChangeDamage(ent, damageChange, true, false, origin, targetPart: TargetBodyPart.All, splitDamage: SplitDamageBehavior.SplitEnsureAllOrganic, ignoreBlockers: true); // iss14: split heals across organic parts (Goob semantics)
    }

    /// <summary>
    /// Tries to get damage from an entity with an optional group specifier.
    /// </summary>
    /// <param name="ent">Entity we're checking the damage on</param>
    /// <param name="amount">Amount we want the damage to be greater than ideally</param>
    /// <param name="damage">Damage specifier we're returning with</param>
    /// <param name="group">An optional group, note that if it fails to index it will just use all damage.</param>
    /// <returns>True if the total damage is greater than the specified amount</returns>
    public bool TryGetDamageGreaterThan(Entity<DamageableComponent> ent,
        FixedPoint2 amount,
        out DamageSpecifier damage,
        ProtoId<DamageGroupPrototype>? group = null)
    {
        // get the damage should be healed (either all or only from one group)
        damage = group == null ? GetPositiveDamage(ent) : GetPositiveDamage(ent, group.Value);

        // If trying to heal more than the total damage of damageEntity just heal everything
        return damage.GetTotal() > amount;
    }

    /// <summary>
    /// Returns a <see cref="DamageSpecifier"/> with all positive damage of the entity from the group specified
    /// </summary>
    /// <param name="ent">entity with damage</param>
    /// <param name="group">group of damage to get values from</param>
    /// <returns></returns>
    public DamageSpecifier GetPositiveDamage(Entity<DamageableComponent> ent, ProtoId<DamageGroupPrototype> group)
    {
        // No damage if no group exists...
        if (!ProtoMan.Resolve(group, out var groupProto))
            return new DamageSpecifier();

        var damage = new DamageSpecifier();
        damage.DamageDict.EnsureCapacity(groupProto.DamageTypes.Count);

        foreach (var damageId in groupProto.DamageTypes)
        {
            if (!ent.Comp.Damage.DamageDict.TryGetValue(damageId, out var value))
                continue;
            if (value > FixedPoint2.Zero)
                damage.DamageDict.Add(damageId, value);
        }

        return damage;
    }

    /// <summary>
    /// Returns a <see cref="DamageSpecifier"/> with all positive damage of the entity
    /// </summary>
    /// <param name="ent">entity with damage</param>
    /// <returns></returns>
    public DamageSpecifier GetPositiveDamage(Entity<DamageableComponent> ent)
    {
        var damage = new DamageSpecifier();
        damage.DamageDict.EnsureCapacity(ent.Comp.Damage.DamageDict.Count);

        foreach (var (damageId, value) in ent.Comp.Damage.DamageDict)
        {
            if (value > FixedPoint2.Zero)
                damage.DamageDict.Add(damageId, value);
        }

        return damage;
    }

    /// <summary>
    /// Applies the two universal "All" modifiers, if set.
    /// Individual damage source modifiers are set in their respective code.
    /// </summary>
    /// <param name="damage">The damage to be changed.</param>
    public DamageSpecifier ApplyUniversalAllModifiers(DamageSpecifier damage)
    {
        // Checks for changes first since they're unlikely in normal play.
        if (
            MathHelper.CloseToPercent(UniversalAllDamageModifier, 1f) &&
            MathHelper.CloseToPercent(UniversalAllHealModifier, 1f)
        )
            return damage;

        foreach (var (key, value) in damage.DamageDict)
        {
            if (value == 0)
                continue;

            if (value > 0)
            {
                damage.DamageDict[key] *= UniversalAllDamageModifier;

                continue;
            }

            if (value < 0)
                damage.DamageDict[key] *= UniversalAllHealModifier;
        }

        return damage;
    }

    public void ClearAllDamage(Entity<DamageableComponent?> ent)
    {
        SetAllDamage(ent, FixedPoint2.Zero);
    }

    /// <summary>
    ///     Sets all damage types supported by a <see cref="Components.DamageableComponent"/> to the specified value.
    /// </summary>
    /// <remarks>
    ///     Does nothing If the given damage value is negative.
    /// </remarks>
    public void SetAllDamage(Entity<DamageableComponent?> ent, FixedPoint2 newValue)
    {
        if (!_damageableQuery.Resolve(ent, ref ent.Comp, false))
            return;

        if (newValue < 0)
            return;

        // Shitmed Change Start - If entity has a body, set damage on all body parts
        if (_bodyQuery.HasComp(ent))
        {
            foreach (var (part, _) in _body.GetBodyChildren(ent))
            {
                if (!_damageableQuery.TryComp(part, out var partDamageable))
                    continue;

                // I LOVE RECURSION!!!
                SetAllDamage((part, partDamageable), newValue);
            }
        }
        // Shitmed Change End

        foreach (var type in ent.Comp.Damage.DamageDict.Keys)
        {
            ent.Comp.Damage.DamageDict[type] = newValue;
        }

        // Setting damage does not count as 'dealing' damage, even if it is set to a larger value, so we pass an
        // empty damage delta.
        OnEntityDamageChanged((ent, ent.Comp), new DamageSpecifier());

        // Shitmed Change Start
        if (_woundableQuery.TryComp(ent, out var woundable))
        {
            _wounds.UpdateWoundableIntegrity(ent, woundable);

            // Create wounds if damage was applied
            if (newValue > 0 && woundable.AllowWounds)
            {
                foreach (var (type, value) in ent.Comp.Damage.DamageDict)
                {
                    _wounds.TryInduceWound(ent,
                        type,
                        value * ent.Comp.Damage.WoundSeverityMultipliers.GetValueOrDefault(type, 1),
                        out _,
                        woundable);
                }
            }
        }
        // Shitmed Change End
    }

    /// <summary>
    /// Set's the damage modifier set prototype for this entity.
    /// </summary>
    /// <param name="ent">The entity we're setting the modifier set of.</param>
    /// <param name="damageModifierSetId">The prototype we're setting.</param>
    public void SetDamageModifierSetId(Entity<DamageableComponent?> ent, ProtoId<DamageModifierSetPrototype>? damageModifierSetId)
    {
        if (!_damageableQuery.Resolve(ent, ref ent.Comp, false))
            return;

        ent.Comp.DamageModifierSetId = damageModifierSetId;

        foreach (var (id, part) in _body.GetBodyChildren(ent)) // Goobstation
        {
            var partDamageable = EnsureComp<DamageableComponent>(id);
            partDamageable.DamageModifierSetId = damageModifierSetId;
            Dirty(id, partDamageable); // iss14 fix: network the part's new modifier set
        }

        Dirty(ent);
    }

    /// <summary>
    /// Gets the damages currently sustained by an entity.
    /// </summary>
    [Obsolete("Do not rely on the ability to determine a numerically quantifiable amount of damage")]
    public DamageSpecifier GetAllDamage(Entity<DamageableComponent?> ent)
    {
        if (!_damageableQuery.Resolve(ent, ref ent.Comp))
            return new();

        return ent.Comp.Damage.Clone();
    }

    /// <summary>
    /// Gets the total amount of damage currently sustained by an entity.
    /// </summary>
    [Obsolete("Do not rely on the ability to determine a numerically quantifiable amount of damage")]
    public FixedPoint2 GetTotalDamage(Entity<DamageableComponent?> ent)
    {
        if (!_damageableQuery.Resolve(ent, ref ent.Comp, false))
            return FixedPoint2.Zero;

        return ent.Comp.TotalDamage;
    }

    /// <summary>
    /// Gets the total amount of damage currently sustained by an entity, indexed by damage group.
    /// </summary>
    [Obsolete("Do not rely on the ability to determine a numerically quantifiable amount of damage")]
    public IReadOnlyDictionary<ProtoId<DamageGroupPrototype>, FixedPoint2> GetDamagePerGroup(Entity<DamageableComponent?> ent)
    {
        if (!_damageableQuery.Resolve(ent, ref ent.Comp))
            return new Dictionary<ProtoId<DamageGroupPrototype>, FixedPoint2>();

        return ent.Comp.DamagePerGroup;
    }

    /// <summary>
    /// Returns whether the entity can be damaged by the given type of damage
    /// </summary>
    [Obsolete("Do not rely on the ability to determine if an entity will be able to be damaged by something")]
    public bool CanBeDamagedBy(Entity<InjurableComponent?> ent, ProtoId<DamageTypePrototype> type)
    {
        if (!_injurableQuery.Resolve(ent, ref ent.Comp, false))
            return false;

        return SupportsType(ent.Comp.DamageContainer, type);
    }
}
