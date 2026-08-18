using System.Linq;
using Content.Server.DeviceNetwork;
using Content.Server.DeviceNetwork.Systems;
using Content.Server.Silicons.StationAi; // iss14 (from Starlight)
using Content.Shared.PowerCell;
using Content.Shared.DeviceNetwork;
using Content.Shared.DeviceNetwork.Events;
using Content.Shared.Medical.CrewMonitoring;
using Content.Shared.Medical.SuitSensor;
using Content.Shared.Pinpointer;
using Content.Shared.Silicons.StationAi; // iss14 (from Starlight)
using Robust.Server.GameObjects;

namespace Content.Server.Medical.CrewMonitoring;

public sealed partial class CrewMonitoringConsoleSystem : EntitySystem
{
    [Dependency] private PowerCellSystem _cell = default!;
    [Dependency] private UserInterfaceSystem _uiSystem = default!;
    [Dependency] private StationAiSystem _stationAi = default!; // iss14 (from Starlight)

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<CrewMonitoringConsoleComponent, ComponentRemove>(OnRemove);
        SubscribeLocalEvent<CrewMonitoringConsoleComponent, DeviceNetworkPacketEvent>(OnPacketReceived);
        SubscribeLocalEvent<CrewMonitoringConsoleComponent, BoundUIOpenedEvent>(OnUIOpened);
        SubscribeLocalEvent<CrewMonitoringConsoleComponent, CrewMonitoringWarpRequestMessage>(OnWarpRequest); // iss14 (from Starlight)
    }

    /// <summary>
    /// iss14 (from Starlight): an AI viewing the crew monitor clicked the map - warp its eye there.
    /// </summary>
    private void OnWarpRequest(EntityUid uid, CrewMonitoringConsoleComponent component, CrewMonitoringWarpRequestMessage args)
    {
        if (args.Actor is not { Valid: true } actor)
            return;

        if (!HasComp<StationAiHeldComponent>(actor))
        {
            Log.Warning($"{ToPrettyString(actor)} attempted to warp via crew monitor {ToPrettyString(uid)} without being a station AI.");
            return;
        }

        var coordinates = GetCoordinates(args.Coordinates);
        if (!coordinates.IsValid(EntityManager))
            return;

        _stationAi.TryWarpEyeToCoordinates(actor, coordinates);
    }

    private void OnRemove(EntityUid uid, CrewMonitoringConsoleComponent component, ComponentRemove args)
    {
        component.ConnectedSensors.Clear();
    }

    private void OnPacketReceived(EntityUid uid, CrewMonitoringConsoleComponent component, DeviceNetworkPacketEvent args)
    {
        var payload = args.Data;

        // Check command
        if (!payload.TryGetValue(DeviceNetworkConstants.Command, out string? command))
            return;

        if (command != DeviceNetworkConstants.CmdUpdatedState)
            return;

        if (!payload.TryGetValue(SuitSensorConstants.NET_STATUS_COLLECTION, out Dictionary<string, SuitSensorStatus>? sensorStatus))
            return;

        component.ConnectedSensors = sensorStatus;
        UpdateUserInterface(uid, component);
    }

    private void OnUIOpened(EntityUid uid, CrewMonitoringConsoleComponent component, BoundUIOpenedEvent args)
    {
        if (!_cell.TryUseActivatableCharge(uid))
            return;

        UpdateUserInterface(uid, component);
    }

    private void UpdateUserInterface(EntityUid uid, CrewMonitoringConsoleComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        if (!_uiSystem.IsUiOpen(uid, CrewMonitoringUIKey.Key))
            return;

        // The grid must have a NavMapComponent to visualize the map in the UI
        var xform = Transform(uid);

        if (xform.GridUid != null)
            EnsureComp<NavMapComponent>(xform.GridUid.Value);

        // Update all sensors info
        var allSensors = component.ConnectedSensors.Values.ToList();
        _uiSystem.SetUiState(uid, CrewMonitoringUIKey.Key, new CrewMonitoringState(allSensors));
    }
}
