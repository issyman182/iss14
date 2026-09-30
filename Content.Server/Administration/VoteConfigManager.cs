using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.Json;
using Content.Shared.GameTicking.Prototypes;
using Content.Server.Maps;
using Content.Shared.Administration;
using Content.Shared.CCVar;
using Content.Shared.Maps;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Server.Administration;

/// <summary>
/// Admin-editable, persistent configuration for voting: which vote types are enabled, plus named profiles of
/// maps and gamemodes. The active map profile overrides the map pool (drives both random-on-restart selection
/// and map voting); the active gamemode profile is the random-on-restart source and filters preset voting.
/// Persisted as JSON to the server user-data dir.
/// </summary>
public sealed partial class VoteConfigManager
{
    [Dependency] private IResourceManager _res = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private IGameMapManager _maps = default!;
    [Dependency] private IRobustRandom _random = default!;

    private static readonly ResPath Dir = new("/vote_config");
    private static readonly ResPath ConfigFile = new("/vote_config/config.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private ISawmill _sawmill = default!;

    private bool _togglesPersisted;
    private readonly Dictionary<VoteToggle, bool> _toggles = new()
    {
        [VoteToggle.Enabled] = true,
        [VoteToggle.Restart] = true,
        [VoteToggle.Preset] = true,
        [VoteToggle.Map] = false,
        [VoteToggle.Votekick] = true,
    };

    private bool _filterMapsByPlayerCount;

    // Vote-duration timers (seconds). Clamped to a sane range when set.
    private const int MinTimer = 1;
    private const int MaxTimer = 600;
    private bool _timersPersisted;
    private readonly Dictionary<VoteTimer, int> _timers = new()
    {
        [VoteTimer.Restart] = 60,
        [VoteTimer.Preset] = 30,
        [VoteTimer.Map] = 90,
        [VoteTimer.Alone] = 10,
        [VoteTimer.Votekick] = 45,
    };

    private string _activeMapProfile = "";
    private readonly Dictionary<string, List<string>> _mapProfiles = new();

    // Per-map player-count range overrides (map id -> min/max). Max 0 = unlimited. Maps without an
    // entry fall back to the prototype's MinPlayers/MaxPlayers.
    private readonly Dictionary<string, MapPlayerRange> _mapPlayerRanges = new();

    private string _activePresetProfile = "";
    private readonly Dictionary<string, List<string>> _presetProfiles = new();

    public void Initialize()
    {
        _sawmill = Logger.GetSawmill("vote_config");
        Load();
    }

    /// <summary>Applies persisted state (toggles + map pool) after systems are up.</summary>
    public void PostInitialize()
    {
        if (_togglesPersisted)
        {
            foreach (var (toggle, value) in _toggles)
                _cfg.SetCVar(ToggleCVar(toggle), value);
        }

        if (_timersPersisted)
        {
            foreach (var (timer, value) in _timers)
                _cfg.SetCVar(TimerCVar(timer), value);
        }

        _maps.RuntimeMapPoolFilterByPlayerCount = _filterMapsByPlayerCount;
        ApplyMapPlayerRanges();
        ApplyMapPool();
    }

    #region Toggles

    public bool GetToggle(VoteToggle toggle) => _cfg.GetCVar(ToggleCVar(toggle));

    public void SetToggle(VoteToggle toggle, bool value)
    {
        _cfg.SetCVar(ToggleCVar(toggle), value);
        _toggles[toggle] = value;
        _togglesPersisted = true;
        Save();
    }

    public bool FilterMapsByPlayerCount => _filterMapsByPlayerCount;

    public void SetFilterMapsByPlayerCount(bool value)
    {
        _filterMapsByPlayerCount = value;
        _maps.RuntimeMapPoolFilterByPlayerCount = value;
        Save();
    }

    private static CVarDef<bool> ToggleCVar(VoteToggle toggle)
    {
        return toggle switch
        {
            VoteToggle.Enabled => CCVars.VoteEnabled,
            VoteToggle.Restart => CCVars.VoteRestartEnabled,
            VoteToggle.Preset => CCVars.VotePresetEnabled,
            VoteToggle.Map => CCVars.VoteMapEnabled,
            VoteToggle.Votekick => CCVars.VotekickEnabled,
            _ => CCVars.VoteEnabled,
        };
    }

    #endregion

    #region Timers

    public int GetTimer(VoteTimer timer) => _cfg.GetCVar(TimerCVar(timer));

    public void SetTimer(VoteTimer timer, int value)
    {
        var clamped = Math.Clamp(value, MinTimer, MaxTimer);
        _cfg.SetCVar(TimerCVar(timer), clamped);
        _timers[timer] = clamped;
        _timersPersisted = true;
        Save();
    }

    private static CVarDef<int> TimerCVar(VoteTimer timer)
    {
        return timer switch
        {
            VoteTimer.Restart => CCVars.VoteTimerRestart,
            VoteTimer.Preset => CCVars.VoteTimerPreset,
            VoteTimer.Map => CCVars.VoteTimerMap,
            VoteTimer.Alone => CCVars.VoteTimerAlone,
            VoteTimer.Votekick => CCVars.VotekickTimer,
            _ => CCVars.VoteTimerMap,
        };
    }

    #endregion

    #region Profiles

    public IReadOnlyList<string> MapProfileNames => Names(_mapProfiles);
    public IReadOnlyList<string> PresetProfileNames => Names(_presetProfiles);
    public string ActiveMapProfile => _activeMapProfile;
    public string ActivePresetProfile => _activePresetProfile;

    public bool IsItemIncluded(bool isMap, string itemId)
    {
        var (profiles, active) = Get(isMap);
        return active.Length != 0 && profiles.TryGetValue(active, out var list) && list.Contains(itemId);
    }

    public void SetActiveProfile(bool isMap, string profile)
    {
        var (profiles, _) = Get(isMap);
        // Empty string means "(default)" — no override.
        var resolved = profile.Length != 0 && profiles.ContainsKey(profile) ? profile : "";

        if (isMap)
            _activeMapProfile = resolved;
        else
            _activePresetProfile = resolved;

        ApplyMapPool();
        Save();
    }

    public void CreateProfile(bool isMap, string name)
    {
        var safe = Sanitize(name);
        if (safe.Length == 0)
            return;

        var (profiles, _) = Get(isMap);
        if (!profiles.ContainsKey(safe))
            profiles[safe] = new List<string>();

        if (isMap)
            _activeMapProfile = safe;
        else
            _activePresetProfile = safe;

        ApplyMapPool();
        Save();
    }

    public void DeleteProfile(bool isMap)
    {
        var (profiles, active) = Get(isMap);
        if (active.Length == 0)
            return;

        profiles.Remove(active);
        if (isMap)
            _activeMapProfile = "";
        else
            _activePresetProfile = "";

        ApplyMapPool();
        Save();
    }

    public void SetItem(bool isMap, string itemId, bool included)
    {
        var (profiles, active) = Get(isMap);
        if (active.Length == 0 || !profiles.TryGetValue(active, out var list))
            return;

        if (included)
        {
            if (!list.Contains(itemId))
                list.Add(itemId);
        }
        else
        {
            list.Remove(itemId);
        }

        ApplyMapPool();
        Save();
    }

    private (Dictionary<string, List<string>> Profiles, string Active) Get(bool isMap)
    {
        return isMap ? (_mapProfiles, _activeMapProfile) : (_presetProfiles, _activePresetProfile);
    }

    #endregion

    #region Map player ranges

    public bool TryGetMapPlayerRange(string mapId, out int min, out int max)
    {
        if (_mapPlayerRanges.TryGetValue(mapId, out var range))
        {
            min = range.Min;
            max = range.Max;
            return true;
        }

        min = 0;
        max = 0;
        return false;
    }

    /// <summary>Sets a per-map player-count range override. Max 0 (or less) means unlimited.</summary>
    public void SetMapPlayerRange(string mapId, int min, int max)
    {
        if (!_proto.HasIndex<GameMapPrototype>(mapId))
            return;

        min = Math.Max(0, min);
        max = Math.Max(0, max);
        if (max != 0 && max < min)
            max = min;

        _mapPlayerRanges[mapId] = new MapPlayerRange { Min = min, Max = max };
        ApplyMapPlayerRanges();
        Save();
    }

    /// <summary>Removes a per-map override so the map falls back to its prototype MinPlayers/MaxPlayers.</summary>
    public void ClearMapPlayerRange(string mapId)
    {
        if (!_mapPlayerRanges.Remove(mapId))
            return;

        ApplyMapPlayerRanges();
        Save();
    }

    private void ApplyMapPlayerRanges()
    {
        _maps.SetRuntimeMapPlayerRanges(_mapPlayerRanges.Count == 0
            ? null
            : _mapPlayerRanges.ToDictionary(kv => kv.Key, kv => (kv.Value.Min, kv.Value.Max)));
    }

    private static IReadOnlyList<string> Names(Dictionary<string, List<string>> profiles)
    {
        return profiles.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    #endregion

    #region Hooks consumed by the game systems

    /// <summary>The active gamemode set, or null when no profile is active (used to filter preset voting).</summary>
    public IReadOnlySet<string>? GetActivePresetSet()
    {
        if (_activePresetProfile.Length != 0
            && _presetProfiles.TryGetValue(_activePresetProfile, out var list)
            && list.Count > 0)
            return list.ToHashSet();

        return null;
    }

    /// <summary>Picks a random gamemode from the active profile, for random-on-restart selection.</summary>
    public bool TryGetRandomPreset([NotNullWhen(true)] out string? presetId)
    {
        presetId = null;
        if (_activePresetProfile.Length == 0 || !_presetProfiles.TryGetValue(_activePresetProfile, out var list))
            return false;

        var valid = list.Where(id => _proto.HasIndex<GamePresetPrototype>(id)).ToList();
        if (valid.Count == 0)
            return false;

        presetId = _random.Pick(valid);
        return true;
    }

    private void ApplyMapPool()
    {
        if (_activeMapProfile.Length != 0
            && _mapProfiles.TryGetValue(_activeMapProfile, out var list)
            && list.Count > 0)
        {
            // Only include maps that still exist.
            _maps.SetRuntimeMapPool(list.Where(id => _proto.HasIndex<GameMapPrototype>(id)).ToHashSet());
        }
        else
        {
            _maps.SetRuntimeMapPool(null);
        }
    }

    #endregion

    #region Persistence

    private void Save()
    {
        try
        {
            _res.UserData.CreateDir(Dir);
            using var writer = _res.UserData.OpenWriteText(ConfigFile);
            writer.Write(JsonSerializer.Serialize(BuildData(), JsonOpts));
        }
        catch (Exception e)
        {
            _sawmill.Error($"Failed to write vote config: {e}");
        }
    }

    private void Load()
    {
        if (!_res.UserData.Exists(ConfigFile))
            return;

        try
        {
            using var reader = _res.UserData.OpenText(ConfigFile);
            var data = JsonSerializer.Deserialize<ConfigData>(reader.ReadToEnd(), JsonOpts);
            if (data == null)
                return;

            _togglesPersisted = data.TogglesPersisted;
            _toggles[VoteToggle.Enabled] = data.VoteEnabled;
            _toggles[VoteToggle.Restart] = data.RestartVote;
            _toggles[VoteToggle.Preset] = data.PresetVote;
            _toggles[VoteToggle.Map] = data.MapVote;
            _toggles[VoteToggle.Votekick] = data.VotekickVote;

            _timersPersisted = data.TimersPersisted;
            _timers[VoteTimer.Restart] = data.RestartTimer;
            _timers[VoteTimer.Preset] = data.PresetTimer;
            _timers[VoteTimer.Map] = data.MapTimer;
            _timers[VoteTimer.Alone] = data.AloneTimer;
            _timers[VoteTimer.Votekick] = data.VotekickTimer;

            _filterMapsByPlayerCount = data.FilterMapsByPlayerCount;

            _activeMapProfile = data.ActiveMapProfile;
            _mapProfiles.Clear();
            foreach (var (k, v) in data.MapProfiles)
                _mapProfiles[k] = v;

            _mapPlayerRanges.Clear();
            foreach (var (k, v) in data.MapPlayerRanges)
                _mapPlayerRanges[k] = v;

            _activePresetProfile = data.ActivePresetProfile;
            _presetProfiles.Clear();
            foreach (var (k, v) in data.PresetProfiles)
                _presetProfiles[k] = v;
        }
        catch (Exception e)
        {
            _sawmill.Error($"Failed to read vote config: {e}");
        }
    }

    private ConfigData BuildData()
    {
        return new ConfigData
        {
            TogglesPersisted = _togglesPersisted,
            VoteEnabled = _toggles[VoteToggle.Enabled],
            RestartVote = _toggles[VoteToggle.Restart],
            PresetVote = _toggles[VoteToggle.Preset],
            MapVote = _toggles[VoteToggle.Map],
            VotekickVote = _toggles[VoteToggle.Votekick],
            TimersPersisted = _timersPersisted,
            RestartTimer = _timers[VoteTimer.Restart],
            PresetTimer = _timers[VoteTimer.Preset],
            MapTimer = _timers[VoteTimer.Map],
            AloneTimer = _timers[VoteTimer.Alone],
            VotekickTimer = _timers[VoteTimer.Votekick],
            FilterMapsByPlayerCount = _filterMapsByPlayerCount,
            ActiveMapProfile = _activeMapProfile,
            MapProfiles = _mapProfiles.ToDictionary(kv => kv.Key, kv => kv.Value),
            MapPlayerRanges = _mapPlayerRanges.ToDictionary(kv => kv.Key, kv => kv.Value),
            ActivePresetProfile = _activePresetProfile,
            PresetProfiles = _presetProfiles.ToDictionary(kv => kv.Key, kv => kv.Value),
        };
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

    private sealed class ConfigData
    {
        public bool TogglesPersisted { get; set; }
        public bool VoteEnabled { get; set; } = true;
        public bool RestartVote { get; set; } = true;
        public bool PresetVote { get; set; } = true;
        public bool MapVote { get; set; }
        public bool VotekickVote { get; set; } = true;
        public bool TimersPersisted { get; set; }
        public int RestartTimer { get; set; } = 60;
        public int PresetTimer { get; set; } = 30;
        public int MapTimer { get; set; } = 90;
        public int AloneTimer { get; set; } = 10;
        public int VotekickTimer { get; set; } = 45;
        public bool FilterMapsByPlayerCount { get; set; }
        public string ActiveMapProfile { get; set; } = "";
        public Dictionary<string, List<string>> MapProfiles { get; set; } = new();
        public Dictionary<string, MapPlayerRange> MapPlayerRanges { get; set; } = new();
        public string ActivePresetProfile { get; set; } = "";
        public Dictionary<string, List<string>> PresetProfiles { get; set; } = new();
    }

    /// <summary>A per-map player-count override. Max 0 = unlimited.</summary>
    public sealed class MapPlayerRange
    {
        public int Min { get; set; }
        public int Max { get; set; }
    }

    #endregion
}
