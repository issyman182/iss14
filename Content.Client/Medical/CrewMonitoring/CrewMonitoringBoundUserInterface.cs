using Content.Shared.Medical.CrewMonitoring;
using Content.Shared.Silicons.StationAi; // iss14 (from Starlight)
using Robust.Client.UserInterface;
using Robust.Shared.Map; // iss14 (from Starlight)
using Robust.Shared.Player; // iss14 (from Starlight)

namespace Content.Client.Medical.CrewMonitoring;

public sealed partial class CrewMonitoringBoundUserInterface : BoundUserInterface
{
    [Dependency] private ISharedPlayerManager _playerManager = default!; // iss14 (from Starlight)

    [ViewVariables]
    private CrewMonitoringWindow? _menu;

    public CrewMonitoringBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
        IoCManager.InjectDependencies(this); // iss14 (from Starlight)
    }

    protected override void Open()
    {
        base.Open();

        EntityUid? gridUid = null;
        var stationName = string.Empty;

        if (EntMan.TryGetComponent<TransformComponent>(Owner, out var xform))
        {
            gridUid = xform.GridUid;

            if (EntMan.TryGetComponent<MetaDataComponent>(gridUid, out var metaData))
            {
                stationName = metaData.EntityName;
            }
        }

        _menu = this.CreateWindow<CrewMonitoringWindow>();
        _menu.Set(stationName, gridUid);
        _menu.MapClicked += OnMapClicked; // iss14 (from Starlight)
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        switch (state)
        {
            case CrewMonitoringState st:
                EntMan.TryGetComponent<TransformComponent>(Owner, out var xform);
                _menu?.ShowSensors(st.Sensors, Owner, xform?.Coordinates);
                break;
        }
    }

    // iss14 (from Starlight): an AI viewing the console can click the map to warp its eye there.
    private void OnMapClicked(EntityCoordinates coordinates)
    {
        var local = _playerManager.LocalEntity;

        if (local is null || !EntMan.HasComponent<StationAiHeldComponent>(local.Value))
            return;

        SendMessage(new CrewMonitoringWarpRequestMessage(EntMan.GetNetCoordinates(coordinates)));
    }
}
