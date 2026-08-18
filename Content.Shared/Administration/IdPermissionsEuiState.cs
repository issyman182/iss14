using Content.Shared.Access;
using Content.Shared.Eui;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Administration;

/// <summary>State for the admin ID permissions editor EUI (<c>idpermissions</c> / ID card context verb).</summary>
[Serializable, NetSerializable]
public sealed class IdPermissionsEuiState(
    bool canEdit,
    bool targetValid,
    string targetName,
    string? fullName,
    string? jobTitle,
    string jobProto,
    string jobIcon,
    List<ProtoId<AccessLevelPrototype>> access)
    : EuiStateBase
{
    public readonly bool CanEdit = canEdit;

    /// <summary>False when the target ID card no longer exists.</summary>
    public readonly bool TargetValid = targetValid;

    /// <summary>Entity name of the ID card being edited.</summary>
    public readonly string TargetName = targetName;

    public readonly string? FullName = fullName;
    public readonly string? JobTitle = jobTitle;

    /// <summary>Job prototype id from the station record (or the card itself); empty if unknown.</summary>
    public readonly string JobProto = jobProto;

    /// <summary>Job icon prototype id currently on the card; empty if unknown.</summary>
    public readonly string JobIcon = jobIcon;

    /// <summary>Access tags currently on the card.</summary>
    public readonly List<ProtoId<AccessLevelPrototype>> Access = access;
}

/// <summary>Writes name/job/access to the target ID card. Sent on every edit, like the ID card console.</summary>
[Serializable, NetSerializable]
public sealed class IdPermissionsWriteMessage(
    string fullName,
    string jobTitle,
    string? jobProto,
    string? jobIcon,
    List<ProtoId<AccessLevelPrototype>> access)
    : EuiMessageBase
{
    public readonly string FullName = fullName;
    public readonly string JobTitle = jobTitle;

    /// <summary>Job prototype to apply (sets icon/departments/record job); null = leave unchanged.</summary>
    public readonly string? JobProto = jobProto;

    /// <summary>Job icon to apply, overriding the job preset's icon; null = leave unchanged.</summary>
    public readonly string? JobIcon = jobIcon;

    public readonly List<ProtoId<AccessLevelPrototype>> Access = access;
}
