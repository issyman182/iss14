using Content.Shared.Body.Components;
using Content.Shared.Body.Organ;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;
using Content.Shared.Whitelist;

namespace Content.Shared.EntityEffects.Effects.Body;

/// <summary>
/// Drops or deletes organs matching the category and entity filters.
/// </summary>
/// <remarks>
/// iss14: ported to the fork's (Shitmed) body. Upstream uses organ categories (OrganCategoryPrototype);
/// here a "category" is either a body part type with optional symmetry (e.g. <c>HandLeft</c>, <c>HandRight</c>,
/// <c>Hand</c>), which detaches the matching body part, or an organ slot id (e.g. <c>Heart</c>, <c>Lungs</c>,
/// <c>Stomach</c>, <c>Liver</c>, <c>Kidneys</c>; case-insensitive), which removes the organ in that slot.
/// </remarks>
/// <inheritdoc cref="EntityEffectSystem{T, TEffect}"/>
public sealed partial class RemoveOrgansEntityEffectSystem : EntityEffectSystem<BodyComponent, RemoveOrgans>
{
    [Dependency] private SharedBodySystem _body = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private EntityWhitelistSystem _whitelist = default!;

    protected override void Effect(Entity<BodyComponent> entity, ref EntityEffectEvent<RemoveOrgans> args)
    {
        var categories = args.Effect.Categories;
        var excludedCategories = args.Effect.ExcludedCategories;
        var maxCount = args.Effect.MaxCount;
        var delete = args.Effect.Delete;

        if (maxCount is <= 0)
            return;

        var selected = new List<EntityUid>();

        // Body parts (hands etc.) referenced by category.
        if (categories != null)
        {
            foreach (var category in categories)
            {
                if (!TryParsePart(category, out var partType, out var symmetry))
                    continue;

                foreach (var part in _body.GetBodyChildrenOfType(entity, partType, entity.Comp, symmetry))
                {
                    if (!_whitelist.CheckBoth(part.Id, args.Effect.Blacklist, args.Effect.Whitelist))
                        continue;

                    if (selected.Contains(part.Id))
                        continue;

                    selected.Add(part.Id);
                    if (selected.Count == maxCount)
                        break;
                }

                if (selected.Count == maxCount)
                    break;
            }
        }

        // Organs.
        if (selected.Count != maxCount)
        {
            foreach (var organ in _body.GetBodyOrgans(entity, entity.Comp))
            {
                if (!_whitelist.CheckBoth(organ.Id, args.Effect.Blacklist, args.Effect.Whitelist))
                    continue;

                var slot = organ.Component.SlotId;
                if (categories != null && !MatchesSlot(categories, slot))
                    continue;

                if (MatchesSlot(excludedCategories, slot))
                    continue;

                selected.Add(organ.Id);
                if (selected.Count == maxCount)
                    break;
            }
        }

        foreach (var uid in selected)
        {
            if (delete)
            {
                PredictedQueueDel(uid);
                continue;
            }

            if (TryComp<OrganComponent>(uid, out var organ))
                _body.RemoveOrgan(uid, organ);
            else if (HasComp<BodyPartComponent>(uid))
                _transform.AttachToGridOrMap(uid);
        }
    }

    private static bool MatchesSlot(IEnumerable<string> categories, string slotId)
    {
        foreach (var category in categories)
        {
            if (string.Equals(category, slotId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool TryParsePart(string category, out BodyPartType type, out BodyPartSymmetry? symmetry)
    {
        type = BodyPartType.Other;
        symmetry = null;

        var name = category;
        if (name.EndsWith("Left", StringComparison.OrdinalIgnoreCase))
        {
            symmetry = BodyPartSymmetry.Left;
            name = name[..^4];
        }
        else if (name.EndsWith("Right", StringComparison.OrdinalIgnoreCase))
        {
            symmetry = BodyPartSymmetry.Right;
            name = name[..^5];
        }

        // Chest/Groin/Head are vital, never detach them through this effect.
        if (!Enum.TryParse(name, true, out type) || type is BodyPartType.Other or BodyPartType.Chest or BodyPartType.Groin or BodyPartType.Head)
            return false;

        return true;
    }
}

/// <inheritdoc cref="EntityEffect"/>
public sealed partial class RemoveOrgans : EntityEffectBase<RemoveOrgans>
{
    /// <summary>
    /// Categories to remove: body part types (optionally suffixed Left/Right) or organ slot ids.
    /// Null allows any organ (body parts are never selected then).
    /// </summary>
    [DataField]
    public HashSet<string>? Categories;

    /// <summary>
    /// Organ slot ids to keep, even if included in Categories.
    /// </summary>
    [DataField]
    public HashSet<string> ExcludedCategories = [];

    /// <summary>
    /// Additional filter for the organs, not the body.
    /// </summary>
    [DataField]
    public EntityWhitelist? Whitelist;

    /// <summary>
    /// Organs to keep regardless of the other filters.
    /// </summary>
    [DataField]
    public EntityWhitelist? Blacklist;

    /// <summary>
    /// Delete selected organs instead of dropping them.
    /// </summary>
    [DataField]
    public bool Delete;

    /// <summary>
    /// Maximum number to remove in body enumeration order. Null removes all matches; zero or less removes none.
    /// </summary>
    [DataField]
    public int? MaxCount;
}
