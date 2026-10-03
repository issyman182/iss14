using Content.Shared.Eui;
using Robust.Shared.Serialization;

namespace Content.Shared.Administration;

/// <summary>Which standard vote a checkbox toggles.</summary>
public enum VoteToggle : byte
{
    Enabled,
    Restart,
    Preset,
    Map,
    Votekick,
}

/// <summary>Which vote-duration timer (in seconds) a field edits.</summary>
public enum VoteTimer : byte
{
    Restart,
    Preset,
    Map,
    Alone,
    Votekick,
}

/// <summary>A selectable map or gamemode row: its id, display name, and whether it's in the active profile.</summary>
[Serializable, NetSerializable]
public struct VoteConfigItem(string id, string display, bool included)
{
    public string Id = id;
    public string Display = display;
    public bool Included = included;
}

/// <summary>
/// A per-map player-count range row: prototype defaults plus the admin override, if any.
/// Max 0 = unlimited.
/// </summary>
[Serializable, NetSerializable]
public struct VoteConfigMapRange(string id, string display, int protoMin, int protoMax, bool hasOverride, int min, int max)
{
    public string Id = id;
    public string Display = display;
    public int ProtoMin = protoMin;
    public int ProtoMax = protoMax;
    public bool HasOverride = hasOverride;
    public int Min = min;
    public int Max = max;
}

/// <summary>State for the voting config admin EUI (<c>votingconfig</c>).</summary>
[Serializable, NetSerializable]
public sealed class VoteConfigEuiState(
    bool canEdit,
    bool voteEnabled,
    bool restartVote,
    bool presetVote,
    bool mapVote,
    bool votekickVote,
    bool filterMapsByPlayerCount,
    int restartTimer,
    int presetTimer,
    int mapTimer,
    int aloneTimer,
    int votekickTimer,
    List<string> mapProfiles,
    string activeMapProfile,
    List<VoteConfigItem> maps,
    List<string> presetProfiles,
    string activePresetProfile,
    List<VoteConfigItem> presets,
    List<VoteConfigMapRange> mapRanges,
    string fallbackPreset,
    string fallbackMap)
    : EuiStateBase
{
    public readonly bool CanEdit = canEdit;

    public readonly bool VoteEnabled = voteEnabled;
    public readonly bool RestartVote = restartVote;
    public readonly bool PresetVote = presetVote;
    public readonly bool MapVote = mapVote;
    public readonly bool VotekickVote = votekickVote;

    public readonly bool FilterMapsByPlayerCount = filterMapsByPlayerCount;

    public readonly int RestartTimer = restartTimer;
    public readonly int PresetTimer = presetTimer;
    public readonly int MapTimer = mapTimer;
    public readonly int AloneTimer = aloneTimer;
    public readonly int VotekickTimer = votekickTimer;

    public readonly List<string> MapProfiles = mapProfiles;
    public readonly string ActiveMapProfile = activeMapProfile;
    public readonly List<VoteConfigItem> Maps = maps;

    public readonly List<string> PresetProfiles = presetProfiles;
    public readonly string ActivePresetProfile = activePresetProfile;
    public readonly List<VoteConfigItem> Presets = presets;

    public readonly List<VoteConfigMapRange> MapRanges = mapRanges;

    /// <summary>Fallback gamemode id, empty = none.</summary>
    public readonly string FallbackPreset = fallbackPreset;

    /// <summary>Fallback map id, empty = none (prototype fallback maps).</summary>
    public readonly string FallbackMap = fallbackMap;
}

// ---- Client -> server messages ----

[Serializable, NetSerializable]
public sealed class VoteConfigSetToggleMessage(VoteToggle toggle, bool value) : EuiMessageBase
{
    public readonly VoteToggle Toggle = toggle;
    public readonly bool Value = value;
}

[Serializable, NetSerializable]
public sealed class VoteConfigSetMapPlayerCountFilterMessage(bool value) : EuiMessageBase
{
    public readonly bool Value = value;
}

[Serializable, NetSerializable]
public sealed class VoteConfigSetTimerMessage(VoteTimer timer, int value) : EuiMessageBase
{
    public readonly VoteTimer Timer = timer;
    public readonly int Value = value;
}

[Serializable, NetSerializable]
public sealed class VoteConfigSetActiveProfileMessage(bool isMap, string profile) : EuiMessageBase
{
    public readonly bool IsMap = isMap;
    public readonly string Profile = profile;
}

[Serializable, NetSerializable]
public sealed class VoteConfigCreateProfileMessage(bool isMap, string name) : EuiMessageBase
{
    public readonly bool IsMap = isMap;
    public readonly string Name = name;
}

[Serializable, NetSerializable]
public sealed class VoteConfigDeleteProfileMessage(bool isMap) : EuiMessageBase
{
    public readonly bool IsMap = isMap;
}

[Serializable, NetSerializable]
public sealed class VoteConfigSetItemMessage(bool isMap, string itemId, bool included) : EuiMessageBase
{
    public readonly bool IsMap = isMap;
    public readonly string ItemId = itemId;
    public readonly bool Included = included;
}

/// <summary>Sets the fallback map (<paramref name="isMap"/>) or fallback gamemode. Empty id = none.</summary>
[Serializable, NetSerializable]
public sealed class VoteConfigSetFallbackMessage(bool isMap, string id) : EuiMessageBase
{
    public readonly bool IsMap = isMap;
    public readonly string Id = id;
}

/// <summary>Sets (or clears) a per-map player-count range override. Max 0 = unlimited.</summary>
[Serializable, NetSerializable]
public sealed class VoteConfigSetMapRangeMessage(string mapId, int min, int max, bool clear) : EuiMessageBase
{
    public readonly string MapId = mapId;
    public readonly int Min = min;
    public readonly int Max = max;
    public readonly bool Clear = clear;
}
