// iss14
using System.Globalization;
using System.Linq;
using System.Numerics;
using Content.Server.Administration.Logs;
using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Shared.Administration;
using Content.Shared.Database;
using Content.Shared.Economy;
using Content.Shared.Economy.Tasks;
using Content.Shared.Eui;
using Content.Shared.Item;
using Content.Shared.Mobs.Components;
using Content.Shared.Pinpointer;
using Content.Shared.Power.Components;
using Content.Shared.Storage.Components;
using Content.Shared.Warps;
using Robust.Shared.Map;
using Robust.Shared.Player;

namespace Content.Server.Administration;

/// <summary>
/// Server side of the entity finder (<c>entityfinder</c>): lists entities by name/prototype, category and distance
/// from the admin, with their grid and world positions, and teleports the admin to a chosen one. Station grids are
/// placed at a random offset and rotation every round, so the map file's <c>pos</c> (grid position) never matches
/// <c>tp</c> coordinates (world position); this window shows both.
/// </summary>
public sealed partial class EntityFinderEui : BaseEui
{
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private IEntityManager _entMan = default!;
    [Dependency] private IAdminLogManager _adminLog = default!;

    private const int RowLimit = 250;

    private EntityFinderEuiState _state = new(new(), 0, string.Empty, string.Empty);
    private EntityFinderSearchMessage _lastSearch = new(string.Empty, EntityFinderCategory.All, 0f, true);

    public EntityFinderEui()
    {
        IoCManager.InjectDependencies(this);
    }

    private bool CanUse() => _admins.HasAdminFlag(Player, AdminFlags.Admin);

    public override EuiStateBase GetNewState() => _state;

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        if (!CanUse())
        {
            Close();
            return;
        }

        switch (msg)
        {
            case EntityFinderSearchMessage search:
                _lastSearch = search;
                Search(string.Empty);
                break;
            case EntityFinderTeleportMessage tp:
                Teleport(tp);
                break;
        }
    }

    private static string Format(Vector2 v) => $"{v.X.ToString("0.##", CultureInfo.InvariantCulture)}, {v.Y.ToString("0.##", CultureInfo.InvariantCulture)}";

    private bool Matches(EntityFinderCategory category, EntityUid uid, TransformComponent xform)
    {
        return category switch
        {
            EntityFinderCategory.All => true,
            EntityFinderCategory.Anchored => xform.Anchored,
            EntityFinderCategory.Machines => _entMan.HasComponent<SharedApcPowerReceiverComponent>(uid),
            EntityFinderCategory.Economy => _entMan.HasComponent<CashRegisterComponent>(uid)
                                            || _entMan.HasComponent<TaskBoardComponent>(uid)
                                            || _entMan.HasComponent<AtmComponent>(uid)
                                            || _entMan.HasComponent<FineTerminalComponent>(uid)
                                            || _entMan.HasComponent<ChipExchangerComponent>(uid),
            EntityFinderCategory.Storage => _entMan.HasComponent<EntityStorageComponent>(uid),
            EntityFinderCategory.Items => _entMan.HasComponent<ItemComponent>(uid),
            EntityFinderCategory.Mobs => _entMan.HasComponent<MobStateComponent>(uid),
            EntityFinderCategory.Players => _entMan.HasComponent<ActorComponent>(uid),
            EntityFinderCategory.Beacons => _entMan.HasComponent<NavMapBeaconComponent>(uid) || _entMan.HasComponent<WarpPointComponent>(uid),
            _ => true,
        };
    }

    /// <summary>Runs the last search again and pushes the result.</summary>
    public void Search(string status)
    {
        if (!CanUse())
        {
            Close();
            return;
        }

        var xformSystem = _entMan.System<SharedTransformSystem>();
        var search = _lastSearch;
        var term = search.Search.Trim();

        // Where the admin stands: distances are measured from here.
        MapCoordinates? adminPos = null;
        var adminText = Loc.GetString("entity-finder-admin-nowhere");
        if (Player.AttachedEntity is { } admin && _entMan.TryGetComponent<TransformComponent>(admin, out var adminXform))
        {
            adminPos = xformSystem.GetMapCoordinates(admin, adminXform);
            var grid = adminXform.GridUid != null ? Format(adminXform.LocalPosition) : "-";
            adminText = Loc.GetString("entity-finder-admin-position",
                ("world", Format(adminPos.Value.Position)), ("map", (int) adminPos.Value.MapId), ("grid", grid));
        }

        var rows = new List<(float Distance, EntityFinderRow Row)>();
        var query = _entMan.AllEntityQueryEnumerator<TransformComponent, MetaDataComponent>();
        while (query.MoveNext(out var uid, out var xform, out var meta))
        {
            // Skip maps, grids and things inside containers (their position is their holder's).
            if (xform.MapUid == uid || xform.GridUid == uid || xform.MapID == MapId.Nullspace || xform.ParentUid is { Valid: true } parent && parent != xform.GridUid && parent != xform.MapUid)
                continue;

            if (meta.EntityPrototype is not { } proto)
                continue;

            if (term.Length > 0
                && !meta.EntityName.Contains(term, StringComparison.OrdinalIgnoreCase)
                && !proto.ID.Contains(term, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!Matches(search.Category, uid, xform))
                continue;

            var world = xformSystem.GetMapCoordinates(uid, xform);
            var distance = -1f;
            if (adminPos != null && adminPos.Value.MapId == world.MapId)
                distance = (world.Position - adminPos.Value.Position).Length();
            else if (search.SameMapOnly)
                continue;

            if (search.Radius > 0 && (distance < 0 || distance > search.Radius))
                continue;

            var gridPos = xform.GridUid != null ? Format(xform.LocalPosition) : "-";
            rows.Add((distance, new EntityFinderRow(_entMan.GetNetEntity(uid), meta.EntityName, proto.ID, gridPos,
                Format(world.Position), (int) world.MapId, distance, xform.Anchored)));
        }

        // Nearest first; things on other maps at the end.
        rows.Sort((a, b) =>
        {
            var da = a.Distance < 0 ? float.MaxValue : a.Distance;
            var db = b.Distance < 0 ? float.MaxValue : b.Distance;
            return da.CompareTo(db);
        });

        _state = new EntityFinderEuiState(rows.Take(RowLimit).Select(r => r.Row).ToList(), rows.Count, adminText, status);
        StateDirty();
    }

    private void Teleport(EntityFinderTeleportMessage msg)
    {
        if (Player.AttachedEntity is not { } admin)
        {
            Search(Loc.GetString("entity-finder-no-body"));
            return;
        }

        if (!_entMan.TryGetEntity(msg.Entity, out var target) || !_entMan.TryGetComponent<TransformComponent>(target, out var targetXform))
        {
            Search(Loc.GetString("entity-finder-gone"));
            return;
        }

        var xformSystem = _entMan.System<SharedTransformSystem>();
        // Land next to the target, on its grid when it has one, so the admin ends up where the thing really is.
        var coords = targetXform.Coordinates;
        if (targetXform.GridUid is { } grid && targetXform.ParentUid != grid)
            coords = new EntityCoordinates(grid, Vector2.Transform(xformSystem.GetWorldPosition(target.Value), xformSystem.GetInvWorldMatrix(grid)));

        xformSystem.SetCoordinates(admin, coords);
        _adminLog.Add(LogType.Action, LogImpact.Low, $"{_entMan.ToPrettyString(admin):admin} teleported to {_entMan.ToPrettyString(target.Value):target} via entity finder");
        Search(Loc.GetString("entity-finder-teleported", ("name", _entMan.GetComponent<MetaDataComponent>(target.Value).EntityName)));
    }
}
