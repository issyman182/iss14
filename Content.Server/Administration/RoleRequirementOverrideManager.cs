using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Content.Shared.Administration;
using Content.Shared.CCVar;
using Content.Shared.Roles;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server.Administration;

/// <summary>
/// Holds admin-editable, persistent overrides for job play time requirements, applies them to
/// <see cref="SharedRoleSystem"/>, replicates them to clients, and persists them (plus named profiles) to
/// the server user-data dir as JSON.
/// </summary>
/// <remarks>
/// In-memory the override is a per-job ordered list of requirements (ordering gives stable indices for the
/// editor UI). When applied it is handed to the role system as job-id → requirement set, fully replacing
/// that job's default requirements. Jobs not present keep their defaults. Persistence/replication use a flat
/// <see cref="RoleRequirementDto"/> (time-based requirement types only).
/// </remarks>
public sealed partial class RoleRequirementOverrideManager
{
    [Dependency] private IResourceManager _res = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IEntityManager _entity = default!;
    [Dependency] private IServerNetManager _net = default!;
    [Dependency] private IPlayerManager _players = default!;

    private static readonly ResPath Dir = new("/role_requirement_overrides");
    private static readonly ResPath ActiveFile = new("/role_requirement_overrides/active.json");
    private static readonly ResPath ProfilesDir = new("/role_requirement_overrides/profiles");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private ISawmill _sawmill = default!;

    private bool _enabled = true;
    private bool _roleTimers = true;
    private bool _hasPersistedRoleTimers;
    private readonly Dictionary<string, List<JobRequirement>> _jobs = new();
    private readonly Dictionary<string, List<JobRequirement>> _antags = new();

    private Dictionary<string, List<JobRequirement>> Roles(bool isAntag) => isAntag ? _antags : _jobs;

    public bool OverridesEnabled => _enabled;
    public bool RoleTimersEnabled => _cfg.GetCVar(CCVars.GameRoleTimers);

    /// <summary>Loads persisted overrides from disk. Call after prototypes are available.</summary>
    public void Initialize()
    {
        _sawmill = Logger.GetSawmill("role_req_override");
        _net.RegisterNetMessage<MsgRoleRequirementOverride>();
        _players.PlayerStatusChanged += OnPlayerStatusChanged;
        _roleTimers = _cfg.GetCVar(CCVars.GameRoleTimers);
        Load();
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs args)
    {
        // Replicate the current override to each client as it connects, so its lobby matches the server.
        if (args.NewStatus == SessionStatus.Connected)
            SendOverrideTo(args.Session.Channel);
    }

    /// <summary>Applies the loaded overrides to the role system. Call after entity systems are up.</summary>
    public void PostInitialize()
    {
        if (_hasPersistedRoleTimers)
            _cfg.SetCVar(CCVars.GameRoleTimers, _roleTimers);

        Apply();
    }

    #region Queries

    public bool IsOverridden(string jobId, bool isAntag = false) => Roles(isAntag).ContainsKey(jobId);

    /// <summary>The effective requirements for a job: the override if one exists, otherwise the defaults.</summary>
    public IReadOnlyList<JobRequirement> GetEffectiveRequirements(JobPrototype job)
    {
        if (_jobs.TryGetValue(job.ID, out var list))
            return list;

        return DefaultRequirements(job);
    }

    private List<JobRequirement> DefaultRequirements(JobPrototype job)
    {
        var roleSystem = _entity.System<SharedRoleSystem>();
        var reqs = roleSystem.GetRoleRequirements(job);
        return reqs == null ? new List<JobRequirement>() : reqs.ToList();
    }

    /// <summary>The effective requirements for an antag: the override if one exists, otherwise the defaults.</summary>
    public IReadOnlyList<JobRequirement> GetEffectiveRequirements(AntagPrototype antag)
    {
        if (_antags.TryGetValue(antag.ID, out var list))
            return list;

        return DefaultRequirements(antag);
    }

    private List<JobRequirement> DefaultRequirements(AntagPrototype antag)
    {
        var roleSystem = _entity.System<SharedRoleSystem>();
        var reqs = roleSystem.GetRoleRequirements(antag);
        return reqs == null ? new List<JobRequirement>() : reqs.ToList();
    }

    #endregion

    #region Global toggles

    public void SetOverridesEnabled(bool value)
    {
        _enabled = value;
        Apply();
        Save();
    }

    public void SetRoleTimersEnabled(bool value)
    {
        _roleTimers = value;
        _hasPersistedRoleTimers = true;
        _cfg.SetCVar(CCVars.GameRoleTimers, value);
        Save();
    }

    #endregion

    #region Editing

    /// <summary>Snapshots a role's current effective requirements into the override so it can be edited.</summary>
    private List<JobRequirement> EnsureOverridden(string jobId, bool isAntag)
    {
        var dict = Roles(isAntag);
        if (dict.TryGetValue(jobId, out var existing))
            return existing;

        List<JobRequirement> list;
        if (isAntag)
        {
            list = _proto.TryIndex<AntagPrototype>(jobId, out var antag)
                ? Clone(DefaultRequirements(antag))
                : new List<JobRequirement>();
        }
        else
        {
            list = _proto.TryIndex<JobPrototype>(jobId, out var job)
                ? Clone(DefaultRequirements(job))
                : new List<JobRequirement>();
        }

        dict[jobId] = list;
        return list;
    }

    public void EditTime(string jobId, int index, TimeSpan time, bool isAntag = false)
    {
        var list = EnsureOverridden(jobId, isAntag);
        if (index < 0 || index >= list.Count)
            return;

        switch (list[index])
        {
            case OverallPlaytimeRequirement o: o.Time = time; break;
            case RoleTimeRequirement r: r.Time = time; break;
            case DepartmentTimeRequirement d: d.Time = time; break;
            default: return; // Non-time requirement; not editable here.
        }

        Apply();
        Save();
    }

    public void SetInverted(string jobId, int index, bool inverted, bool isAntag = false)
    {
        var list = EnsureOverridden(jobId, isAntag);
        if (index < 0 || index >= list.Count)
            return;

        list[index].Inverted = inverted;
        Apply();
        Save();
    }

    public void Remove(string jobId, int index, bool isAntag = false)
    {
        var list = EnsureOverridden(jobId, isAntag);
        if (index < 0 || index >= list.Count)
            return;

        list.RemoveAt(index);
        Apply();
        Save();
    }

    public void Add(string jobId, RoleReqKind kind, string target, TimeSpan time, bool inverted, bool isAntag = false)
    {
        JobRequirement? req = kind switch
        {
            RoleReqKind.Overall => new OverallPlaytimeRequirement { Time = time, Inverted = inverted },
            RoleReqKind.Role => new RoleTimeRequirement { Role = target, Time = time, Inverted = inverted },
            RoleReqKind.Department => new DepartmentTimeRequirement { Department = target, Time = time, Inverted = inverted },
            _ => null,
        };

        if (req == null)
            return;

        EnsureOverridden(jobId, isAntag).Add(req);
        Apply();
        Save();
    }

    /// <summary>Reverts a role back to its default (YAML) requirements.</summary>
    public void ResetJob(string jobId, bool isAntag = false)
    {
        if (Roles(isAntag).Remove(jobId))
        {
            Apply();
            Save();
        }
    }

    #endregion

    #region Apply

    private void Apply()
    {
        var roleSystem = _entity.System<SharedRoleSystem>();

        if (_enabled)
        {
            // iss14 fix: the edited lists only hold time requirements (the DTO can't represent anything
            // else), so re-attach the prototype's non-time requirements (whitelist, age, etc.) when
            // applying, otherwise overriding a job's timers silently dropped them.
            var appliedJobs = _jobs.ToDictionary(kv => kv.Key, kv => WithPreservedRequirements(kv.Key, kv.Value, isAntag: false));
            var appliedAntags = _antags.ToDictionary(kv => kv.Key, kv => WithPreservedRequirements(kv.Key, kv.Value, isAntag: true));
            roleSystem.SetRuntimeRequirementOverride(appliedJobs, appliedAntags);
        }
        else
        {
            roleSystem.SetRuntimeRequirementOverride(null);
        }

        BroadcastOverride();
    }

    /// <summary>
    /// iss14 fix: the override list plus every prototype requirement that
    /// <see cref="RoleRequirementDto.FromRequirement"/> cannot represent (non-time requirements).
    /// </summary>
    private HashSet<JobRequirement> WithPreservedRequirements(string id, List<JobRequirement> overrides, bool isAntag)
    {
        var set = overrides.ToHashSet();

        // Prototype Requirements are [Access]-restricted to SharedRoleSystem; go through its accessor.
        var roleSystem = _entity.System<SharedRoleSystem>();
        HashSet<JobRequirement>? protoReqs = null;
        if (isAntag)
        {
            if (_proto.TryIndex<AntagPrototype>(id, out var antag))
                protoReqs = roleSystem.GetDefaultRequirements(antag);
        }
        else if (_proto.TryIndex<JobPrototype>(id, out var job))
        {
            protoReqs = roleSystem.GetDefaultRequirements(job);
        }

        if (protoReqs == null)
            return set;

        foreach (var req in protoReqs)
        {
            if (RoleRequirementDto.FromRequirement(req) == null)
                set.Add(req);
        }

        return set;
    }

    #endregion

    #region Replication

    private void BroadcastOverride()
    {
        _net.ServerSendToAll(new MsgRoleRequirementOverride { Data = SerializeOverride() });
    }

    private void SendOverrideTo(INetChannel channel)
    {
        _net.ServerSendMessage(new MsgRoleRequirementOverride { Data = SerializeOverride() }, channel);
    }

    /// <summary>Serializes the applied override for replication (empty string when disabled).</summary>
    private string SerializeOverride()
    {
        if (!_enabled)
            return string.Empty;

        try
        {
            return RoleRequirementDto.Serialize(RolesToDto(_jobs), RolesToDto(_antags));
        }
        catch (Exception e)
        {
            _sawmill.Error($"Failed to serialize role requirement override for replication: {e}");
            return string.Empty;
        }
    }

    #endregion

    #region Profiles

    public IReadOnlyList<string> ListProfiles()
    {
        if (!_res.UserData.Exists(ProfilesDir))
            return Array.Empty<string>();

        return _res.UserData.DirectoryEntries(ProfilesDir)
            .Where(e => e.EndsWith(".json"))
            .Select(e => e[..^5])
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void SaveProfile(string name)
    {
        var safe = Sanitize(name);
        if (safe.Length == 0)
            return;

        _res.UserData.CreateDir(ProfilesDir);
        WriteFile(ProfilesDir / $"{safe}.json", CurrentData());
    }

    public void LoadProfile(string name)
    {
        var safe = Sanitize(name);
        var path = ProfilesDir / $"{safe}.json";
        if (safe.Length == 0 || !_res.UserData.Exists(path) || !ReadFile(path, out var data))
            return;

        ApplyData(data);
    }

    public void DeleteProfile(string name)
    {
        var safe = Sanitize(name);
        var path = ProfilesDir / $"{safe}.json";
        if (safe.Length != 0 && _res.UserData.Exists(path))
            _res.UserData.Delete(path);
    }

    /// <summary>
    /// Imports the requirement_overrides.yml setup (the <see cref="JobRequirementOverridePrototype"/> selected by
    /// <c>game.role_timer_override</c>, defaulting to "Reduced") as the current override.
    /// </summary>
    public void ImportPrototype()
    {
        var id = _cfg.GetCVar(CCVars.GameRoleTimerOverride);
        if (string.IsNullOrEmpty(id))
            id = "Reduced";

        if (!_proto.TryIndex<JobRequirementOverridePrototype>(id, out var proto))
        {
            _sawmill.Warning($"Cannot import: no JobRequirementOverridePrototype '{id}'.");
            return;
        }

        _jobs.Clear();
        foreach (var (jobId, reqs) in proto.Jobs)
        {
            // A job listed with no requirements (e.g. "Chaplain:" with a null value) is a valid override
            // meaning "no requirements" — guard against the null set.
            _jobs[jobId.Id] = Clone(reqs?.ToList() ?? new List<JobRequirement>());
        }

        _antags.Clear();
        foreach (var (antagId, reqs) in proto.Antags)
        {
            _antags[antagId.Id] = Clone(reqs?.ToList() ?? new List<JobRequirement>());
        }

        _sawmill.Info($"Imported {proto.Jobs.Count} job and {proto.Antags.Count} antag overrides from prototype '{id}'.");
        Apply();
        Save();
    }

    #endregion

    #region Persistence

    private void Save()
    {
        _res.UserData.CreateDir(Dir);
        WriteFile(ActiveFile, CurrentData());
    }

    private void Load()
    {
        if (!_res.UserData.Exists(ActiveFile) || !ReadFile(ActiveFile, out var data))
            return;

        _enabled = data.Enabled;
        _roleTimers = data.RoleTimers;
        _hasPersistedRoleTimers = true;
        RolesFromDto(_jobs, data.Jobs);
        RolesFromDto(_antags, data.Antags);
    }

    /// <summary>Applies a loaded data set as the live override and persists it as the active set.</summary>
    private void ApplyData(PersistData data)
    {
        _enabled = data.Enabled;
        _roleTimers = data.RoleTimers;
        _hasPersistedRoleTimers = true;
        RolesFromDto(_jobs, data.Jobs);
        RolesFromDto(_antags, data.Antags);
        _cfg.SetCVar(CCVars.GameRoleTimers, _roleTimers);
        Apply();
        Save();
    }

    private PersistData CurrentData()
    {
        return new PersistData { Enabled = _enabled, RoleTimers = _roleTimers, Jobs = RolesToDto(_jobs), Antags = RolesToDto(_antags) };
    }

    private static Dictionary<string, List<RoleRequirementDto>> RolesToDto(Dictionary<string, List<JobRequirement>> roles)
    {
        var result = new Dictionary<string, List<RoleRequirementDto>>();
        foreach (var (id, reqs) in roles)
        {
            var list = new List<RoleRequirementDto>(reqs.Count);
            foreach (var req in reqs)
            {
                if (RoleRequirementDto.FromRequirement(req) is { } dto)
                    list.Add(dto);
            }

            result[id] = list;
        }

        return result;
    }

    private static void RolesFromDto(Dictionary<string, List<JobRequirement>> roles, Dictionary<string, List<RoleRequirementDto>> dtos)
    {
        roles.Clear();
        foreach (var (id, dtoList) in dtos)
        {
            var list = new List<JobRequirement>(dtoList.Count);
            foreach (var dto in dtoList)
            {
                if (dto.ToRequirement() is { } req)
                    list.Add(req);
            }

            roles[id] = list;
        }
    }

    private void WriteFile(ResPath path, PersistData data)
    {
        try
        {
            using var writer = _res.UserData.OpenWriteText(path);
            writer.Write(JsonSerializer.Serialize(data, JsonOpts));
        }
        catch (Exception e)
        {
            _sawmill.Error($"Failed to write role requirement overrides to {path}: {e}");
        }
    }

    private bool ReadFile(ResPath path, out PersistData data)
    {
        data = new PersistData();
        try
        {
            using var reader = _res.UserData.OpenText(path);
            var parsed = JsonSerializer.Deserialize<PersistData>(reader.ReadToEnd(), JsonOpts);
            if (parsed == null)
                return false;

            data = parsed;
            return true;
        }
        catch (Exception e)
        {
            _sawmill.Error($"Failed to read role requirement overrides from {path}: {e}");
            return false;
        }
    }

    #endregion

    /// <summary>Deep-copies requirements (via DTO) so edits never mutate prototype data.</summary>
    private List<JobRequirement> Clone(List<JobRequirement> source)
    {
        var result = new List<JobRequirement>(source.Count);
        foreach (var req in source)
        {
            if (RoleRequirementDto.FromRequirement(req)?.ToRequirement() is { } clone)
                result.Add(clone);
        }

        return result;
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_' or ' ')
                sb.Append(c);
        }

        return sb.ToString();
    }

    private sealed class PersistData
    {
        public bool Enabled { get; set; } = true;
        public bool RoleTimers { get; set; } = true;
        public Dictionary<string, List<RoleRequirementDto>> Jobs { get; set; } = new();
        public Dictionary<string, List<RoleRequirementDto>> Antags { get; set; } = new();
    }
}
