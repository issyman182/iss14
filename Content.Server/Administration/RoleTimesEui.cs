using System.Linq;
using Content.Server.Administration.Logs;
using Content.Server.Administration.Managers;
using Content.Server.Database;
using Content.Server.EUI;
using Content.Server.Players.PlayTimeTracking;
using Content.Shared.Administration;
using Content.Shared.Database;
using Content.Shared.Eui;
using Content.Shared.Localizations;
using Content.Shared.Players.PlayTimeTracking;
using Content.Shared.Roles;
using Robust.Server.Player;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server.Administration;

/// <summary>
/// Server side of the role time overview. Gathers every play time tracker for a target player
/// (online via <see cref="PlayTimeTrackingManager"/>, offline straight from the database) and lets
/// an admin set any tracker to an absolute value.
/// </summary>
public sealed partial class RoleTimesEui : BaseEui
{
#pragma warning disable IDE0044 // injected by [Dependency]
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private PlayTimeTrackingManager _playTime = default!;
    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private IAdminLogManager _adminLog = default!;
    [Dependency] private IEntityManager _entity = default!;
#pragma warning restore IDE0044

    private readonly LocatedPlayerData _target;

    /// <summary>Header color for the antag pseudo-department group.</summary>
    private const string AntagGroupColor = "#d82f2f";
    private bool _online;
    private List<RoleTimeInfo> _roles = [];

    public RoleTimesEui(LocatedPlayerData target)
    {
        IoCManager.InjectDependencies(this);
        _target = target;
    }

    public override void Opened()
    {
        base.Opened();
        _admins.OnPermsChanged += OnPermsChanged;
    }

    public override void Closed()
    {
        base.Closed();
        _admins.OnPermsChanged -= OnPermsChanged;
    }

    private void OnPermsChanged(AdminPermsChangedEventArgs args)
    {
        if (args.Player == Player)
            LoadTimes();
    }

    public override EuiStateBase GetNewState()
    {
        return new RoleTimesEuiState(_target.UserId, _target.Username, _online, CanEdit(), _roles);
    }

    private bool CanView()
    {
        return _admins.HasAdminFlag(Player, AdminFlags.Moderator);
    }

    private bool CanEdit()
    {
        return _admins.HasAdminFlag(Player, AdminFlags.Moderator);
    }

    /// <summary>
    /// (Re)load all tracker times for the target player and push them to the client.
    /// </summary>
    public async void LoadTimes()
    {
        if (!CanView())
        {
            Close();
            return;
        }

        // Map each job's tracker to the job, so we can resolve friendly names and requirements.
        var trackerToJob = new Dictionary<string, JobPrototype>();
        foreach (var job in _proto.EnumeratePrototypes<JobPrototype>())
        {
            if (!string.IsNullOrEmpty(job.PlayTimeTracker))
                trackerToJob[job.PlayTimeTracker] = job;
        }

        // Map each job to a department, preferring the job's primary department, so we can group the
        // overview the same way the lobby does.
        var jobToDept = new Dictionary<string, DepartmentPrototype>();
        foreach (var dept in _proto.EnumeratePrototypes<DepartmentPrototype>())
        {
            foreach (var jobId in dept.Roles)
            {
                if (!jobToDept.TryGetValue(jobId.Id, out var existing) || (dept.Primary && !existing.Primary))
                    jobToDept[jobId.Id] = dept;
            }
        }

        var roleSystem = _entity.System<SharedRoleSystem>();

        // Pull current times: live in-memory values for online players, otherwise from the DB.
        Dictionary<string, TimeSpan> times;
        if (_player.TryGetSessionById(_target.UserId, out var session) &&
            _playTime.TryGetTrackerTimes(session, out var live))
        {
            _online = true;
            times = new Dictionary<string, TimeSpan>(live);
        }
        else
        {
            _online = false;
            times = (await _db.GetPlayTimes(_target.UserId))
                .ToDictionary(p => p.Tracker, p => p.TimeSpent);
        }

        // Show every known tracker so any role can be set even if the player has 0 time in it,
        // plus any extra trackers the player happens to have recorded.
        var allTrackers = _proto.EnumeratePrototypes<PlayTimeTrackerPrototype>()
            .Select(p => p.ID)
            .ToHashSet();
        foreach (var tracker in times.Keys)
            allTrackers.Add(tracker);

        var roles = new List<RoleTimeInfo>(allTrackers.Count);
        foreach (var tracker in allTrackers)
        {
            var hasJob = trackerToJob.TryGetValue(tracker, out var job);
            var name = hasJob ? job!.LocalizedName : tracker;
            var requirement = hasJob ? SummarizeRequirements(roleSystem.GetRoleRequirements(job!), trackerToJob) : null;

            string? deptName = null;
            var deptColor = Color.Gray.ToHex();
            var deptWeight = int.MinValue;
            if (hasJob && jobToDept.TryGetValue(job!.ID, out var dept))
            {
                deptName = Loc.GetString(dept.Name);
                deptColor = dept.Color.ToHex();
                deptWeight = dept.Weight;
            }

            roles.Add(new RoleTimeInfo(tracker, name, times.GetValueOrDefault(tracker), requirement,
                deptName, deptColor, deptWeight));
        }

        // Antag roles: informational rows (no tracker to edit) showing the requirements and
        // whether this player's current times satisfy them.
        var antagGroupName = Loc.GetString("role-times-antag-group");
        foreach (var antag in _proto.EnumeratePrototypes<AntagPrototype>())
        {
            if (!antag.SetPreference)
                continue;

            var reqs = roleSystem.GetRoleRequirements(antag);
            var summary = SummarizeRequirements(reqs, trackerToJob);
            var met = EvaluateRequirements(reqs, times);

            roles.Add(new RoleTimeInfo("", Loc.GetString(antag.Name), TimeSpan.Zero, summary,
                antagGroupName, AntagGroupColor, int.MinValue + 1, editable: false, metRequirements: met));
        }

        roles.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        _roles = roles;
        StateDirty();
    }

    /// <summary>
    /// Builds a short human-readable "Requires: ..." string from a job's play time requirements,
    /// or null if there are none we display.
    /// </summary>
    private string? SummarizeRequirements(HashSet<JobRequirement>? requirements, Dictionary<string, JobPrototype> trackerToJob)
    {
        if (requirements == null || requirements.Count == 0)
            return null;

        var parts = new List<string>();
        foreach (var req in requirements)
        {
            switch (req)
            {
                case OverallPlaytimeRequirement overall:
                    parts.Add(Loc.GetString(overall.Inverted ? "role-times-req-overall-inverted" : "role-times-req-overall",
                        ("time", ContentLocalizationManager.FormatPlaytime(overall.Time))));
                    break;

                case RoleTimeRequirement role:
                    var roleName = trackerToJob.TryGetValue(role.Role, out var roleJob) ? roleJob.LocalizedName : role.Role.Id;
                    parts.Add(Loc.GetString(role.Inverted ? "role-times-req-role-inverted" : "role-times-req-role",
                        ("time", ContentLocalizationManager.FormatPlaytime(role.Time)),
                        ("role", roleName)));
                    break;

                case DepartmentTimeRequirement dept:
                    var deptName = _proto.TryIndex(dept.Department, out var deptProto) ? Loc.GetString(deptProto.Name) : dept.Department.Id;
                    parts.Add(Loc.GetString(dept.Inverted ? "role-times-req-department-inverted" : "role-times-req-department",
                        ("time", ContentLocalizationManager.FormatPlaytime(dept.Time)),
                        ("department", deptName)));
                    break;
            }
        }

        return parts.Count == 0 ? null : Loc.GetString("role-times-requires", ("reqs", string.Join(", ", parts)));
    }

    /// <summary>
    /// Evaluates time-based requirements against the player's tracker times. Returns null when there
    /// are no requirements we can evaluate. Non-time requirement types (whitelist etc.) are ignored.
    /// </summary>
    private bool? EvaluateRequirements(HashSet<JobRequirement>? requirements, Dictionary<string, TimeSpan> times)
    {
        if (requirements == null || requirements.Count == 0)
            return true;

        bool? met = null;
        foreach (var req in requirements)
        {
            bool satisfied;
            switch (req)
            {
                case OverallPlaytimeRequirement overall:
                    satisfied = times.GetValueOrDefault(PlayTimeTrackingShared.TrackerOverall) >= overall.Time;
                    break;

                case RoleTimeRequirement role:
                    satisfied = times.GetValueOrDefault(role.Role) >= role.Time;
                    break;

                case DepartmentTimeRequirement dept:
                {
                    var total = TimeSpan.Zero;
                    if (_proto.TryIndex(dept.Department, out var deptProto))
                    {
                        foreach (var jobId in deptProto.Roles)
                        {
                            if (_proto.TryIndex(jobId, out var deptJob) && !string.IsNullOrEmpty(deptJob.PlayTimeTracker))
                                total += times.GetValueOrDefault(deptJob.PlayTimeTracker);
                        }
                    }

                    satisfied = total >= dept.Time;
                    break;
                }

                default:
                    continue; // Not a time requirement; can't evaluate here.
            }

            if (req.Inverted)
                satisfied = !satisfied;

            met = (met ?? true) && satisfied;
        }

        return met ?? true;
    }

    // iss14 fix: async so the offline DB write completes before the times are reloaded.
    public override async void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        if (msg is not RoleTimesSetMessage set)
            return;

        if (!CanEdit())
            return;

        if (!_proto.HasIndex<PlayTimeTrackerPrototype>(set.Tracker))
            return;

        var time = set.Time < TimeSpan.Zero ? TimeSpan.Zero : set.Time;

        if (_player.TryGetSessionById(_target.UserId, out var session))
        {
            // Online: the in-memory tracker dict is authoritative and gets autosaved over the DB,
            // so we must edit through the manager. Convert the absolute target into a delta.
            if (!_playTime.TryGetTrackerTimes(session, out var live))
                return; // Play time data not loaded yet; refuse rather than risk a clobbered write.

            // iss14 fix: flush the active trackers first so the unflushed time since the last update is
            // already in `current`; otherwise SaveSession's flush added it on top of the absolute value.
            _playTime.FlushTracker(session);
            var current = live.GetValueOrDefault(set.Tracker);
            _playTime.AddTimeToTracker(session, set.Tracker, time - current);
            _playTime.SaveSession(session);
        }
        else
        {
            // Offline: no in-memory state, so write the absolute value straight to the DB.
            // UpdatePlayTimes does a replace (not an increment), which is exactly what we want.
            // iss14 fix: await so LoadTimes below reads the written value.
            await _db.UpdatePlayTimes([new PlayTimeUpdate(_target.UserId, set.Tracker, time)]);
        }

        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"{Player:actor} set play time tracker {set.Tracker:tracker} of {_target.Username:subject} to {time}");

        LoadTimes();
    }
}
