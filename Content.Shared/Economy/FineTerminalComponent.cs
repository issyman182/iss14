// iss14: Economy
using Content.Shared.CriminalRecords;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace Content.Shared.Economy;

/// <summary>
/// Legal terminal. Security issues fines against station records; the fined person pays with their ID card;
/// a lawyer who has filed a signed <see cref="LegalMandateComponent"/> for a client pays from the client's
/// account and earns <c>economy.lawyer_fee_percent</c>.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class FineTerminalComponent : Component
{
    public const string IdSlotId = "fine-id";
    public const string MandateSlotId = "fine-mandate";

    [DataField]
    public SoundSpecifier? PaySound = new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg");

    [DataField]
    public SoundSpecifier? DenySound = new SoundPathSpecifier("/Audio/Machines/buzz-two.ogg");

    [DataField]
    public uint MaxReasonLength = 200;

    [DataField]
    public int MaxFine = 100_000;

    [ViewVariables]
    public string Message = string.Empty;

    /// <summary>Record currently selected by security in the issue tab.</summary>
    [ViewVariables]
    public uint? SelectedRecord;
}

/// <summary>
/// A legal representation agreement. Signed by the client (whose session is recorded so the signature can't be
/// forged) and the lawyer. Filing it at a fine terminal lets the lawyer pay the client's fines.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class LegalMandateComponent : Component
{
    [ViewVariables]
    public NetUserId? ClientUserId;

    [DataField, AutoNetworkedField]
    public string? ClientName;

    [DataField]
    public string? ClientFingerprint;

    [ViewVariables]
    public NetUserId? LawyerUserId;

    [DataField, AutoNetworkedField]
    public string? LawyerName;

    [DataField, AutoNetworkedField]
    public bool Filed;

    public bool IsComplete => ClientUserId != null && LawyerUserId != null;
}

[Serializable, NetSerializable]
public enum FineTerminalUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class FineTerminalRecordEntry(uint id, string name, int openTotal)
{
    public readonly uint Id = id;
    public readonly string Name = name;
    public readonly int OpenTotal = openTotal;
}

[Serializable, NetSerializable]
public sealed class FineTerminalBuiState : BoundUserInterfaceState
{
    /// <summary>Whether the user may issue fines (security access).</summary>
    public bool CanIssue;

    /// <summary>All records (for issuing), with their open fine totals.</summary>
    public List<FineTerminalRecordEntry> Records = new();

    public uint? SelectedRecord;
    public string? SelectedName;
    public List<CriminalFine> SelectedFines = new();

    /// <summary>Name on the inserted ID card, if any.</summary>
    public string? CardHolder;

    /// <summary>Fines of the inserted card's holder.</summary>
    public List<CriminalFine> OwnFines = new();

    /// <summary>Whether the card holder has lawyer access and a mandate is filed.</summary>
    public bool IsLawyer;
    public string? ClientName;
    public List<CriminalFine> ClientFines = new();
    public float LawyerFeePercent;

    /// <summary>Mandate paper in the slot: null = none; otherwise its state text.</summary>
    public string? MandateStatus;

    public string Message = string.Empty;
}

[Serializable, NetSerializable]
public sealed class FineTerminalSelectRecordMessage(uint? record) : BoundUserInterfaceMessage
{
    public readonly uint? Record = record;
}

[Serializable, NetSerializable]
public sealed class FineTerminalIssueMessage(uint record, int amount, string reason) : BoundUserInterfaceMessage
{
    public readonly uint Record = record;
    public readonly int Amount = amount;
    public readonly string Reason = reason;
}

/// <summary>Security waives (deletes) an open fine.</summary>
[Serializable, NetSerializable]
public sealed class FineTerminalWaiveMessage(uint record, uint fine) : BoundUserInterfaceMessage
{
    public readonly uint Record = record;
    public readonly uint Fine = fine;
}

/// <summary>Pays one of the card holder's own fines.</summary>
[Serializable, NetSerializable]
public sealed class FineTerminalPayOwnMessage(uint fine) : BoundUserInterfaceMessage
{
    public readonly uint Fine = fine;
}

/// <summary>Lawyer pays one of the client's fines from the client's account.</summary>
[Serializable, NetSerializable]
public sealed class FineTerminalPayClientMessage(uint fine) : BoundUserInterfaceMessage
{
    public readonly uint Fine = fine;
}

/// <summary>Files the mandate paper in the slot.</summary>
[Serializable, NetSerializable]
public sealed class FineTerminalFileMandateMessage : BoundUserInterfaceMessage;
