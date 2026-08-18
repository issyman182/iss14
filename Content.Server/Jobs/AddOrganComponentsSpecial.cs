using Content.Shared._Shitmed.BodyEffects;
using Content.Shared._Shitmed.Body.Organ;
using Content.Shared.Body.Systems;
using Content.Shared.Roles;
using JetBrains.Annotations;
using Robust.Shared.Prototypes;

namespace Content.Server.Jobs;

/// <summary>
/// iss14: Adds components to one of the spawned character's organs' OnAdd registry, exactly like
/// the Shitmed surgery steps that use addOrganOnAdd (e.g. lobotomy). The components apply to the
/// body while the organ is in it, and the matching organ surgeries can still cure/detect them.
/// Used by the Lobotomized trait.
/// </summary>
[UsedImplicitly]
public sealed partial class AddOrganComponentsSpecial : JobSpecial
{
    /// <summary>Slot id of the organ to modify, e.g. "brain".</summary>
    [DataField(required: true)]
    public string OrganSlot = string.Empty;

    /// <summary>The components to add to the organ's OnAdd registry.</summary>
    [DataField(required: true)]
    public ComponentRegistry Components { get; private set; } = new();

    public override void AfterEquip(EntityUid mob)
    {
        var entMan = IoCManager.Resolve<IEntityManager>();
        var body = entMan.System<SharedBodySystem>();

        foreach (var (organId, organ) in body.GetBodyOrgans(mob))
        {
            if (organ.SlotId != OrganSlot)
                continue;

            // Mirrors SharedSurgerySystem.HandleOrganModification's add path.
            organ.OnAdd ??= new ComponentRegistry();

            foreach (var (key, comp) in Components)
            {
                organ.OnAdd[key] = comp;
                organ.AddedKeys.Add(key);
            }

            entMan.EnsureComponent<OrganEffectComponent>(organId);
            entMan.EventBus.RaiseLocalEvent(organId, new OrganComponentsModifyEvent(mob, true));
            entMan.Dirty(organId, organ);
        }
    }
}
