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
            foreach (var (partId, _) in body.GetBodyChildrenOfType(mob, target.Part, symmetry: target.Symmetry).ToList())
            {
                // Detach first so the body updates cleanly (sprite layers, hands, standing),
                // then delete the severed part instead of leaving it on the floor.
                if (body.TryDetachPart(partId))
                    entMan.QueueDeleteEntity(partId);
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
