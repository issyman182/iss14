using Content.Shared.Forensics.Components;
using Content.Shared.Weapons.Ranged.Systems;

namespace Content.Server.Forensics;

/// <summary>
/// Applies gunshot residue to a firearm and its user after the firearm is fired.
/// </summary>
public sealed class GunshotResidueSystem : EntitySystem
{
    public override void Initialize()
    {
        SubscribeLocalEvent<GunshotResidueSourceComponent, GunShotEvent>(OnGunShot);
    }

    private void OnGunShot(
        Entity<GunshotResidueSourceComponent> ent,
        ref GunShotEvent args)
    {
        var residue = Loc.GetString(
            "forensic-residue",
            ("adjective", ent.Comp.ResidueAdjective));

        var shooterForensics = EnsureComp<ForensicsComponent>(args.User);
        shooterForensics.Residues.Add(residue);

        var weaponForensics = EnsureComp<ForensicsComponent>(ent.Owner);
        weaponForensics.Residues.Add(residue);
    }
}