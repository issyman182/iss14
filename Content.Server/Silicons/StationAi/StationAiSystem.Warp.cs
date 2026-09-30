// Starlight: AI camera warping, ported from Starlight (https://github.com/ss14Starlight/space-station-14).
// Starlight code is MIT / Starlight License; the Starlight License requires this attribution.
using System.Collections.Generic;
using Content.Shared.Follower;
using Content.Shared.Follower.Components;
using Content.Shared.Humanoid;
using Content.Shared.Medical.SuitSensors;
using Content.Shared.Mobs.Components;
using Content.Shared.Popups;
using Content.Shared.Silicons.StationAi;
using Content.Shared.Warps;
using Robust.Shared.Map;

namespace Content.Server.Silicons.StationAi;

public sealed partial class StationAiSystem
{
    [Dependency] private SharedMapSystem _map = default!; // iss14: IMapManager was removed in engine 283
    [Dependency] private SharedSuitSensorSystem _suitSensors = default!;
    [Dependency] private FollowerSystem _followerSystem = default!;
    [Dependency] private StationAiVisionSystem _aiVision = default!;

    private readonly Dictionary<EntityUid, EntityUid> _activeFollowTargets = new();
    private readonly List<EntityUid> _followTargetsToRemove = new();
    private readonly ISawmill _warpSawmill = Logger.GetSawmill("stationai.warp");
    private float _followCheckAccumulator;
    private const float FollowCheckInterval = 1f;

    private void InitializeWarp()
    {
        SubscribeLocalEvent<SuitSensorComponent, SuitSensorModeChangedEvent>(OnSuitSensorModeChanged);
        SubscribeLocalEvent<FollowerComponent, StoppedFollowingEntityEvent>(OnFollowerStoppedFollowing);
        SubscribeNetworkEvent<StationAiWarpRequestEvent>(OnStationAiWarpRequest);
        SubscribeNetworkEvent<StationAiWarpToTargetEvent>(OnStationAiWarpToTarget);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _followCheckAccumulator += frameTime;
        if (_followCheckAccumulator < FollowCheckInterval)
            return;

        _followCheckAccumulator -= FollowCheckInterval;

        // Early exit if no one is being followed
        if (_activeFollowTargets.Count == 0)
            return;

        // Check if any followed targets are outside camera view
        _followTargetsToRemove.Clear();

        foreach (var (target, follower) in _activeFollowTargets)
        {
            if (!Exists(target) || !Exists(follower))
            {
                _followTargetsToRemove.Add(target);
                continue;
            }

            // Check if target is outside AI camera view
            if (_aiVision.IsOutsideCameraView(target))
            {
                _followerSystem.StopFollowingEntity(follower, target);
                _followTargetsToRemove.Add(target);
            }
        }

        foreach (var target in _followTargetsToRemove)
        {
            _activeFollowTargets.Remove(target);
        }
    }

    private void OnStationAiWarpRequest(StationAiWarpRequestEvent msg, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { Valid: true } actor || !HasComp<StationAiHeldComponent>(actor))
        {
            _warpSawmill.Debug($"Ignoring warp target request from session {args.SenderSession.UserId} without a valid Station AI entity.");
            return;
        }

        if (!TryGetCore(actor, out var coreEntity) || coreEntity.Comp == null)
        {
            _warpSawmill.Warning($"Station AI {Name(actor)} ({actor}) requested warp targets but is not inserted into a core.");
            return;
        }

        var aiStation = _station.GetOwningStation(coreEntity.Owner);
        var targets = new List<StationAiWarpTarget>();

        CollectCrewWarpTargets(actor, aiStation, targets);
        CollectLocationWarpTargets(actor, aiStation, coreEntity.Comp.RemoteEntity, targets);

        if (targets.Count == 0)
            _warpSawmill.Debug($"No warp targets available for Station AI {Name(actor)} ({actor}).");

        RaiseNetworkEvent(new StationAiWarpTargetsEvent(targets), args.SenderSession.Channel);
    }

    private void OnStationAiWarpToTarget(StationAiWarpToTargetEvent msg, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { Valid: true } actor || !HasComp<StationAiHeldComponent>(actor))
        {
            _warpSawmill.Debug($"Ignoring warp request from session {args.SenderSession.UserId} without a valid Station AI entity.");
            return;
        }

        var target = GetEntity(msg.Target);
        if (!Exists(target))
        {
            _warpSawmill.Warning($"Station AI {Name(actor)} ({actor}) attempted to warp to missing entity {msg.Target}.");
            return;
        }

        // iss14 fix: the target is client-supplied; only allow entities the AI would have been offered
        // (same station, crew inside camera view, or a warp point), otherwise the AI could follow/warp to
        // any entity in the game.
        if (!TryGetCore(actor, out var coreEntity) || coreEntity.Comp == null)
        {
            _warpSawmill.Warning($"Station AI {Name(actor)} ({actor}) requested a warp but is not inserted into a core.");
            return;
        }

        var aiStation = _station.GetOwningStation(coreEntity.Owner);
        var allowed = new List<StationAiWarpTarget>();
        CollectCrewWarpTargets(actor, aiStation, allowed);
        CollectLocationWarpTargets(actor, aiStation, coreEntity.Comp.RemoteEntity, allowed);

        var valid = false;
        foreach (var candidate in allowed)
        {
            if (candidate.Target != msg.Target)
                continue;

            valid = true;
            break;
        }

        if (!valid)
        {
            _warpSawmill.Warning($"Station AI {Name(actor)} ({actor}) attempted to warp to non-offered entity {Name(target)} ({target}).");
            return;
        }

        if (!TryWarpEyeToEntity(actor, target))
            _warpSawmill.Debug($"Station AI {Name(actor)} ({actor}) warp to {Name(target)} ({target}) rejected by TryWarpEyeToEntity.");
    }

    /// <summary>
    /// Populates the warp target buffer with crew members whose suit sensors are broadcasting coordinates.
    /// </summary>
    private void CollectCrewWarpTargets(EntityUid actor, EntityUid? aiStation, List<StationAiWarpTarget> buffer)
    {
        var processed = new HashSet<EntityUid>();
        var enumerator = EntityQueryEnumerator<SuitSensorComponent, TransformComponent>();

        while (enumerator.MoveNext(out var sensorUid, out var sensor, out var xform))
        {
            if (sensor.Mode != SuitSensorMode.SensorCords)
                continue;

            var sensorState = _suitSensors.GetSensorState((sensorUid, sensor, xform));
            if (sensorState is not { } status || status.Coordinates == null)
                continue;

            var ownerUid = GetEntity(status.OwnerUid);
            if (!Exists(ownerUid))
                continue;

            if (!processed.Add(ownerUid))
                continue;

            if (!HasComp<HumanoidAppearanceComponent>(ownerUid))
                continue;

            if (aiStation is { } station)
            {
                var ownerStation = _station.GetOwningStation(ownerUid);
                if (ownerStation != station)
                    continue;
            }

            // Don't show crew members outside of camera view
            if (_aiVision.IsOutsideCameraView(ownerUid))
                continue;

            var display = string.IsNullOrWhiteSpace(status.Job)
                ? status.Name
                : $"{status.Name} ({status.Job})";

            buffer.Add(new StationAiWarpTarget(GetNetEntity(ownerUid), display, StationAiWarpTargetType.Crew));
        }
    }

    private void CollectLocationWarpTargets(EntityUid actor, EntityUid? aiStation, EntityUid? remoteEntity, List<StationAiWarpTarget> buffer)
    {
        var query = AllEntityQuery<WarpPointComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var warp, out var _))
        {
            if (remoteEntity == uid)
            {
                _warpSawmill.Debug($"Skipping remote entity {uid} while building warp locations for Station AI {Name(actor)} ({actor}).");
                continue;
            }

            if (string.IsNullOrWhiteSpace(warp.Location))
                continue;

            if (aiStation is { } station)
            {
                var warpStation = _station.GetOwningStation(uid);
                if (warpStation != station)
                {
                    _warpSawmill.Debug($"Skipping warp point {Name(uid)} ({uid}) outside AI station {station}.");
                    continue;
                }
            }

            var name = warp.Location ?? Name(uid);
            buffer.Add(new StationAiWarpTarget(GetNetEntity(uid), name, StationAiWarpTargetType.Location));
        }
    }

    public bool TryWarpEyeToCoordinates(EntityUid user, EntityCoordinates coordinates, bool popupOnFailure = true)
    {
        bool Fail()
        {
            if (popupOnFailure)
                _popups.PopupClient(Loc.GetString("ai-device-not-responding"), user, PopupType.MediumCaution);

            return false;
        }

        if (!HasComp<StationAiHeldComponent>(user))
            return Fail();

        if (!TryGetCore(user, out var coreEntity) || coreEntity.Comp == null)
            return Fail();

        var coreUid = coreEntity.Owner;

        if (!TryComp<StationAiCoreComponent>(coreUid, out var core))
            return Fail();

        if (!core.Remote)
            SwitchRemoteEntityMode((coreUid, core), true);

        if (!TryComp<StationAiCoreComponent>(coreUid, out core) || core.RemoteEntity is not { Valid: true } remoteEye)
            return Fail();

        if (!_xforms.IsValid(coordinates))
            return Fail();

        var mapCoordinates = _xforms.ToMapCoordinates(coordinates, logError: false);

        if (mapCoordinates == MapCoordinates.Nullspace)
            return Fail();

        if (!_map.TryFindGridAt(mapCoordinates, out var gridUid, out _))
            return Fail();

        var aiStation = _station.GetOwningStation(coreUid);

        if (aiStation != null)
        {
            var targetStation = _station.GetOwningStation(gridUid);

            if (targetStation != aiStation)
                return Fail();
        }

        var targetCoords = _xforms.ToCoordinates((gridUid, Transform(gridUid)), mapCoordinates);

        if (!_xforms.IsValid(targetCoords))
            return Fail();

        _xforms.SetCoordinates(remoteEye, targetCoords);
        _xforms.AttachToGridOrMap(remoteEye, Transform(remoteEye));
        return true;
    }

    public bool TryWarpEyeToEntity(EntityUid user, EntityUid target, bool popupOnFailure = true)
    {
        bool Fail()
        {
            if (popupOnFailure)
                _popups.PopupClient(Loc.GetString("ai-device-not-responding"), user, PopupType.MediumCaution);

            return false;
        }

        if (!TryGetCore(user, out var coreEntity) || coreEntity.Comp == null)
            return Fail();

        if (!TryComp<StationAiCoreComponent>(coreEntity.Owner, out var core))
            return Fail();

        if (!core.Remote)
            SwitchRemoteEntityMode((coreEntity.Owner, core), true);

        if (!TryComp<StationAiCoreComponent>(coreEntity.Owner, out core) || core.RemoteEntity is not { Valid: true } remoteEye)
            return Fail();

        var remoteXform = Transform(remoteEye);

        if ((TryComp(target, out WarpPointComponent? warp) && warp.Follow) || HasComp<MobStateComponent>(target))
        {
            // iss14 edit: no orbit-following here (Starlight only orbits for non-AI remote eye consoles).
            _followerSystem.StartFollowingEntity(remoteEye, target);
            _activeFollowTargets[target] = remoteEye;
            return true;
        }

        if (HasComp<FollowerComponent>(remoteEye))
        {
            var parent = remoteXform.ParentUid;
            if (parent.IsValid())
            {
                _followerSystem.StopFollowingEntity(remoteEye, parent);
                if (_activeFollowTargets.TryGetValue(parent, out var follower) && follower == remoteEye)
                    _activeFollowTargets.Remove(parent);
            }
        }

        return TryWarpEyeToCoordinates(user, Transform(target).Coordinates, popupOnFailure);
    }

    private void OnSuitSensorModeChanged(EntityUid uid, SuitSensorComponent sensor, SuitSensorModeChangedEvent args)
    {
        if (args.Mode == SuitSensorMode.SensorCords)
            return;

        if (sensor.User is not { } wearer || !_activeFollowTargets.TryGetValue(wearer, out var follower))
            return;

        if (TryComp(follower, out FollowerComponent? followerComp) && followerComp.Following == wearer)
            _followerSystem.StopFollowingEntity(follower, wearer);

        _activeFollowTargets.Remove(wearer);
    }

    private void OnFollowerStoppedFollowing(Entity<FollowerComponent> ent, ref StoppedFollowingEntityEvent args)
    {
        if (_activeFollowTargets.TryGetValue(args.Following, out var follower) && follower == args.Follower)
            _activeFollowTargets.Remove(args.Following);
    }
}
