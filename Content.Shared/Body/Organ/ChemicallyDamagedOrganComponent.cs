using Robust.Shared.GameStates;

namespace Content.Shared.Body.Organ;

/// <summary>
/// iss14: marks an organ that has accumulated chemical damage (drinking, smoking, drug abuse).
/// While present, the organ slowly regenerates that damage as long as its body is alive;
/// the component removes itself once the damage is gone. Heavier damage is faster to inflict
/// than to regenerate, so sustained abuse still needs surgery to fix.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class ChemicallyDamagedOrganComponent : Component
{
    /// <summary>
    /// Integrity points regenerated per second while the body is alive.
    /// </summary>
    [DataField]
    public float RegenPerSecond = 0.02f;
}
