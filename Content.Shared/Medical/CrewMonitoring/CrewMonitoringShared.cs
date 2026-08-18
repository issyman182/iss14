using Content.Shared.Medical.SuitSensor;
using Robust.Shared.Map;
using Robust.Shared.Serialization;

namespace Content.Shared.Medical.CrewMonitoring;

[Serializable, NetSerializable]
public enum CrewMonitoringUIKey
{
    Key
}

/// <summary>
/// iss14 (from Starlight): sent by an AI viewing the crew monitor to warp its eye to a clicked map position.
/// </summary>
[Serializable, NetSerializable]
public sealed class CrewMonitoringWarpRequestMessage : BoundUserInterfaceMessage
{
    public NetCoordinates Coordinates;

    public CrewMonitoringWarpRequestMessage(NetCoordinates coordinates)
    {
        Coordinates = coordinates;
    }
}

[Serializable, NetSerializable]
public sealed class CrewMonitoringState : BoundUserInterfaceState
{
    public List<SuitSensorStatus> Sensors;

    public CrewMonitoringState(List<SuitSensorStatus> sensors)
    {
        Sensors = sensors;
    }
}
