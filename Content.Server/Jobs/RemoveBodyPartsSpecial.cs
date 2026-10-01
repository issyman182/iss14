using System.Linq;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;
using Content.Shared.Roles;
using JetBrains.Annotations;

namespace Content.Server.Jobs;

/// <summary>
/// iss14: Detaches and deletes body parts from the spawned character.
/// Used by the missing-limb traits.
/// </summary>
[UsedImplicitly]
public sealed partial class RemoveBodyPartsSpecial : JobSpecial
{
    /// <summary>
    /// The body parts to remove.
    /// </summary>
    [DataField(required: true)]
    public List<BodyPartTarget> Parts { get; private set; } = new();

    public override void AfterEquip(EntityUid mob)
    {
        var entMan = IoCManager.Resolve<IEntityManager>();
        var body = entMan.System<SharedBodySystem>();

        foreach (var target in Parts)
        {
            // ToList: don't modify the body while enumerating it.
            foreach (var (partId, partComp) in body.GetBodyChildrenOfType(mob, target.Part, symmetry: target.Symmetry).ToList())
            {
                // Detaching a part only raises BodyPartRemovedEvent for that part itself; parts attached
                // to it (e.g. the hand on an arm) silently leave the body, so the hands system never
                // removes the usable hand. Detach the deepest parts first (hand, then arm) so every
                // part gets its removal event while it is still attached to the body.
                var parts = body.GetBodyPartChildren(partId, partComp).Select(p => p.Id).ToList();
                parts.Reverse();

                foreach (var part in parts)
                {
                    // Detach first so the body updates cleanly (sprite layers, hands, standing),
                    // then delete the severed part instead of leaving it on the floor.
                    if (body.TryDetachPart(part))
                        entMan.QueueDeleteEntity(part);
                }
            }
        }
    }
}

/// <summary>iss14: A body part type + symmetry pair for <see cref="RemoveBodyPartsSpecial"/>.</summary>
[DataDefinition]
public sealed partial class BodyPartTarget
{
    [DataField(required: true)]
    public BodyPartType Part;

    /// <summary>Which side to remove; null removes all parts of the type.</summary>
    [DataField]
    public BodyPartSymmetry? Symmetry;
}
