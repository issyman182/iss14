using System.Linq;
using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Shared.Administration;
using Content.Shared.Eui;
using Content.Shared.Localizations;
using Content.Shared.Players.PlayTimeTracking;
using Content.Shared.Roles;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.Server.Administration;

/// <summary>
/// Server side of the role requirement editor. Lists jobs (grouped by department) with their effective
/// play time requirements and lets an admin add/remove/edit them, toggle the master role timer switch and
/// the override switch, and manage persistent profiles. All edits go through
/// <see cref="RoleRequirementOverrideManager"/>, which applies and persists them.
/// </summary>
public sealed partial class RoleReqEditorEui : BaseEui
{
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private RoleRequirementOverrideManager _overrides = default!;

    public RoleReqEditorEui()
    {
        IoCManager.InjectDependencies(this);
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
            BuildState();
    }

    private bool CanView() => _admins.HasAdminFlag(Player, AdminFlags.Server);
    private bool CanEdit() => _admins.HasAdminFlag(Player, AdminFlags.Server);

    public override EuiStateBase GetNewState() => _state;

    private RoleReqEditorState _state = new(false, true, true, new(), new());

    /// <summary>Header color for the antag pseudo-department group.</summary>
    private const string AntagGroupColor = "#d82f2f";

    public void BuildState()
    {
        if (!CanView())
        {
            Close();
            return;
        }

        // Friendly role names for RoleTimeRequirement targets.
        var trackerToJobName = new Dictionary<string, string>();
        foreach (var j in _proto.EnumeratePrototypes<JobPrototype>())
        {
            if (!string.IsNullOrEmpty(j.PlayTimeTracker))
                trackerToJobName[j.PlayTimeTracker] = j.LocalizedName;
        }

        // Job -> primary department, for grouping and headers.
        var jobToDept = new Dictionary<string, DepartmentPrototype>();
        foreach (var dept in _proto.EnumeratePrototypes<DepartmentPrototype>())
        {
            foreach (var jobId in dept.Roles)
            {
                if (!jobToDept.TryGetValue(jobId.Id, out var existing) || (dept.Primary && !existing.Primary))
                    jobToDept[jobId.Id] = dept;
            }
        }

        // Show jobs that belong to a department (the playable set, matching the lobby), plus any job that
        // currently has an override even if it has no department.
        // iss14: also show department-less jobs that ship with requirements (e.g. the hidden GifSender
        // pseudo-job that gates GIF chat), so admins can tune them here.
        var jobInfos = new List<RoleReqJobInfo>();
        foreach (var job in _proto.EnumeratePrototypes<JobPrototype>())
        {
            var hasDept = jobToDept.TryGetValue(job.ID, out var dept);
            var reqs = _overrides.GetEffectiveRequirements(job);
            if (!hasDept && !_overrides.IsOverridden(job.ID) && reqs.Count == 0)
                continue;

            var entries = new List<RoleReqEntry>();
            for (var i = 0; i < reqs.Count; i++)
                entries.Add(BuildEntry(i, reqs[i], trackerToJobName));

            jobInfos.Add(new RoleReqJobInfo(
                job.ID,
                job.LocalizedName,
                _overrides.IsOverridden(job.ID),
                hasDept ? Loc.GetString(dept!.Name) : null,
                hasDept ? dept!.Color.ToHex() : Color.Gray.ToHex(),
                hasDept ? dept!.Weight : int.MinValue,
                entries));
        }

        jobInfos.Sort((a, b) => string.Compare(a.JobName, b.JobName, StringComparison.OrdinalIgnoreCase));

        // Antag roles get their own pseudo-department group at the bottom of the list.
        var antagInfos = new List<RoleReqJobInfo>();
        foreach (var antag in _proto.EnumeratePrototypes<AntagPrototype>())
        {
            // Only player-selectable antags (matching the lobby), plus anything already overridden.
            if (!antag.SetPreference && !_overrides.IsOverridden(antag.ID, isAntag: true))
                continue;

            var entries = new List<RoleReqEntry>();
            var reqs = _overrides.GetEffectiveRequirements(antag);
            for (var i = 0; i < reqs.Count; i++)
                entries.Add(BuildEntry(i, reqs[i], trackerToJobName));

            antagInfos.Add(new RoleReqJobInfo(
                antag.ID,
                Loc.GetString(antag.Name),
                _overrides.IsOverridden(antag.ID, isAntag: true),
                Loc.GetString("role-req-editor-antag-group"),
                AntagGroupColor,
                int.MinValue + 1,
                entries,
                isAntag: true));
        }

        antagInfos.Sort((a, b) => string.Compare(a.JobName, b.JobName, StringComparison.OrdinalIgnoreCase));
        jobInfos.AddRange(antagInfos);

        _state = new RoleReqEditorState(
            CanEdit(),
            _overrides.RoleTimersEnabled,
            _overrides.OverridesEnabled,
            _overrides.ListProfiles().ToList(),
            jobInfos);

        StateDirty();
    }

    private RoleReqEntry BuildEntry(int index, JobRequirement req, Dictionary<string, string> trackerToJobName)
    {
        switch (req)
        {
            case OverallPlaytimeRequirement o:
                return new RoleReqEntry(index, RoleReqKind.Overall, "", o.Time, o.Inverted, true,
                    Loc.GetString(o.Inverted ? "role-times-req-overall-inverted" : "role-times-req-overall",
                        ("time", ContentLocalizationManager.FormatPlaytime(o.Time))));

            case RoleTimeRequirement r:
                var roleName = trackerToJobName.GetValueOrDefault(r.Role, r.Role.Id);
                return new RoleReqEntry(index, RoleReqKind.Role, r.Role.Id, r.Time, r.Inverted, true,
                    Loc.GetString(r.Inverted ? "role-times-req-role-inverted" : "role-times-req-role",
                        ("time", ContentLocalizationManager.FormatPlaytime(r.Time)), ("role", roleName)));

            case DepartmentTimeRequirement d:
                var deptName = _proto.TryIndex(d.Department, out var deptProto) ? Loc.GetString(deptProto.Name) : d.Department.Id;
                return new RoleReqEntry(index, RoleReqKind.Department, d.Department.Id, d.Time, d.Inverted, true,
                    Loc.GetString(d.Inverted ? "role-times-req-department-inverted" : "role-times-req-department",
                        ("time", ContentLocalizationManager.FormatPlaytime(d.Time)), ("department", deptName)));

            default:
                return new RoleReqEntry(index, RoleReqKind.Other, "", TimeSpan.Zero, req.Inverted, false,
                    Loc.GetString("role-req-editor-other", ("type", req.GetType().Name)));
        }
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        // The two view-only-safe path: everything here mutates, so require edit perms.
        if (msg is not (RoleReqSetTimersEnabledMessage or RoleReqSetOverridesEnabledMessage or RoleReqEditTimeMessage
            or RoleReqSetInvertedMessage or RoleReqRemoveMessage or RoleReqAddMessage or RoleReqResetJobMessage
            or RoleReqSaveProfileMessage or RoleReqLoadProfileMessage or RoleReqDeleteProfileMessage
            or RoleReqImportPrototypeMessage))
            return;

        if (!CanEdit())
            return;

        switch (msg)
        {
            case RoleReqSetTimersEnabledMessage m:
                _overrides.SetRoleTimersEnabled(m.Value);
                break;
            case RoleReqSetOverridesEnabledMessage m:
                _overrides.SetOverridesEnabled(m.Value);
                break;
            case RoleReqEditTimeMessage m:
                _overrides.EditTime(m.JobId, m.Index, m.Time, m.IsAntag);
                break;
            case RoleReqSetInvertedMessage m:
                _overrides.SetInverted(m.JobId, m.Index, m.Inverted, m.IsAntag);
                break;
            case RoleReqRemoveMessage m:
                _overrides.Remove(m.JobId, m.Index, m.IsAntag);
                break;
            case RoleReqAddMessage m:
                if (IsValidTarget(m.Kind, m.Target))
                    _overrides.Add(m.JobId, m.Kind, m.Target, m.Time, m.Inverted, m.IsAntag);
                break;
            case RoleReqResetJobMessage m:
                _overrides.ResetJob(m.JobId, m.IsAntag);
                break;
            case RoleReqSaveProfileMessage m:
                _overrides.SaveProfile(m.Name);
                break;
            case RoleReqLoadProfileMessage m:
                _overrides.LoadProfile(m.Name);
                break;
            case RoleReqDeleteProfileMessage m:
                _overrides.DeleteProfile(m.Name);
                break;
            case RoleReqImportPrototypeMessage:
                _overrides.ImportPrototype();
                break;
        }

        BuildState();
    }

    private bool IsValidTarget(RoleReqKind kind, string target)
    {
        return kind switch
        {
            RoleReqKind.Overall => true,
            RoleReqKind.Role => _proto.HasIndex<PlayTimeTrackerPrototype>(target),
            RoleReqKind.Department => _proto.HasIndex<DepartmentPrototype>(target),
            _ => false,
        };
    }
}
