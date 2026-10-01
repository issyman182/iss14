// Ported from Starlight (https://github.com/ss14Starlight/space-station-14).
// Starlight code is MIT / Starlight License; the Starlight License requires this attribution.
using Content.Shared.Silicons.Laws;
using Robust.Shared.GameStates;


namespace Content.Shared._Starlight.Silicons.Borgs;

/// <summary>
/// This means a AI can take it over and then shunt back into their old body.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class StationAIShuntComponent : Component
{
    /// <summary>
    /// What Station-AI will we be returning to after un-shunting
    /// </summary>
    [ViewVariables]
    [DataField, AutoNetworkedField]
    public EntityUid? Return = null;

    /// <summary>
    /// Holds the euid of the action so we can delete it when shunting out.
    /// </summary>
    [ViewVariables]
    [DataField, AutoNetworkedField]
    public EntityUid? ReturnAction = null;

    /// <summary>
    /// what was the lawset of the chassis before the AI shunted into it.
    /// </summary>
    [ViewVariables]
    [DataField, AutoNetworkedField]
    public SiliconLawset? OldLawset = null;

    /// <summary>
    /// iss14: If true, this body is permanently closed to shunting once a player (not a shunted AI)
    /// has occupied it, even if that player later ghosts or leaves the server.
    /// </summary>
    [DataField]
    public bool LockOnPlayer = false;

    /// <summary>
    /// iss14: Set once a player has occupied this body while <see cref="LockOnPlayer"/> is enabled.
    /// </summary>
    [ViewVariables]
    [DataField, AutoNetworkedField]
    public bool PlayerClaimed = false;
}
