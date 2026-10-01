// Ported from Starlight (https://github.com/ss14Starlight/space-station-14).
// Starlight code is MIT / Starlight License; the Starlight License requires this attribution.
using Content.Shared.Actions.Components;
using Content.Shared.Whitelist;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Starlight.Silicons.Borgs;

/// <summary>
/// This comp is added to station AI's invisible posibrain to handle shunting logics.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class StationAIShuntableComponent : Component
{
    /// <summary>
    /// what action is granted to the ai to allow them to return to their body.
    /// </summary>
    [DataField]
    public EntProtoId<ActionComponent> UnshuntAction = "ActionAIUnShunt";

    /// <summary>
    /// What body is this AI currently inhabiting, null if the AI is in it's core.
    /// </summary>
    [ViewVariables]
    public EntityUid? Inhabited = null;

    /// <summary>
    /// iss14: If set, only bodies matching this whitelist can be shunted into
    /// (e.g. the mothership core may only enter xenoborgs).
    /// </summary>
    [DataField]
    public EntityWhitelist? Whitelist;

    /// <summary>
    /// iss14: Bodies matching this blacklist can never be shunted into
    /// (e.g. the station AI may not enter xenoborgs).
    /// </summary>
    [DataField]
    public EntityWhitelist? Blacklist;

    /// <summary>
    /// iss14: Whether the target has to be visible to the station AI camera network.
    /// Disabled for shunters that are not station AIs, like the mothership core.
    /// </summary>
    [DataField]
    public bool RequireCameraView = true;
}
