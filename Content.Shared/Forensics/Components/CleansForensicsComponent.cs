using Robust.Shared.GameStates;

namespace Content.Shared.Forensics.Components;

/// <summary>
/// This component is for items that can clean up forensic evidence
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class CleansForensicsComponent : Component
{
    /// <summary>
    /// How long it takes to wipe prints/blood/etc. off of things using this entity
    /// </summary>
    [DataField]
    public TimeSpan CleanDelay = TimeSpan.FromSeconds(12.0f);

    // Stuff below made by brodiesodie
    [DataField]
    public bool CleanStandardEvidence = true;

    [DataField]
    public bool CleanResidues;

    [DataField]
    public List<LocId> ResiduesToClean = new();
}

