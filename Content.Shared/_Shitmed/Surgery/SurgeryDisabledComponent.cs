using Robust.Shared.GameStates;

namespace Content.Shared._Shitmed.Medical.Surgery;

/// <summary>
/// iss14: Marker component that disables a surgery prototype without removing it.
/// Disabled surgeries are excluded from <see cref="SharedSurgerySystem.AllSurgeries"/>
/// (so they never show up in the surgery UI or the autodoc picker) and always fail
/// validation as a safety net.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class SurgeryDisabledComponent : Component;
