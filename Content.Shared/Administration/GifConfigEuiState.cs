// iss14: GIFs in chat via GifSnap
using Content.Shared.Eui;
using Robust.Shared.Serialization;

namespace Content.Shared.Administration;

/// <summary>Which GIF config value a panel field edits.</summary>
public enum GifConfigField : byte
{
    Enabled,
    SendPeriod,
    SendCount,
    SearchPeriod,
    SearchCount,
    FrameWidth,
    FrameHeight,
    MaxFrames,
    MaxSheetBytes,
    AllowOoc,
    AllowLooc,
    RequiredHours,
}

/// <summary>State for the GIF config admin EUI (<c>gifconfig</c>).</summary>
[Serializable, NetSerializable]
public sealed class GifConfigEuiState(
    bool canEdit,
    bool enabled,
    float sendPeriod,
    int sendCount,
    float searchPeriod,
    int searchCount,
    int frameWidth,
    int frameHeight,
    int maxFrames,
    int maxSheetBytes,
    bool allowOoc,
    bool allowLooc,
    bool roleExists,
    float requiredHours,
    string status)
    : EuiStateBase
{
    public readonly bool CanEdit = canEdit;
    public readonly bool Enabled = enabled;
    public readonly float SendPeriod = sendPeriod;
    public readonly int SendCount = sendCount;
    public readonly float SearchPeriod = searchPeriod;
    public readonly int SearchCount = searchCount;
    public readonly int FrameWidth = frameWidth;
    public readonly int FrameHeight = frameHeight;
    public readonly int MaxFrames = maxFrames;
    public readonly int MaxSheetBytes = maxSheetBytes;
    public readonly bool AllowOoc = allowOoc;
    public readonly bool AllowLooc = allowLooc;

    /// <summary>Whether the <c>gifs.role</c> job prototype exists (if not, there is no playtime gate to edit).</summary>
    public readonly bool RoleExists = roleExists;

    /// <summary>Overall playtime (hours) currently required by the gate job; 0 if it has no such requirement.</summary>
    public readonly float RequiredHours = requiredHours;

    public readonly string Status = status;
}

/// <summary>Sets one GIF config value. The server parses <see cref="Value"/> per <see cref="Field"/>.</summary>
[Serializable, NetSerializable]
public sealed class GifConfigSetMessage(GifConfigField field, string value) : EuiMessageBase
{
    public readonly GifConfigField Field = field;
    public readonly string Value = value;
}
