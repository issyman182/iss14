namespace Content.Shared.Forensics.Components;

/// <summary>
/// Marks a firearm as producing forensic residue when fired.
/// </summary>
// Credit to brodysodie.
[RegisterComponent]
public sealed partial class GunshotResidueSourceComponent : Component
{
    [DataField]
    public LocId ResidueAdjective = "residue-gunshot";
}