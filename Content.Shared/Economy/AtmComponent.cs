// iss14: Economy
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Economy;

/// <summary>
/// A bank terminal. Insert an ID card bound to an account (<see cref="BankCardComponent"/>) to see the
/// balance, withdraw cash or deposit the cash stack in the cash slot.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class AtmComponent : Component
{
    public const string IdSlotId = "atm-id";
    public const string CashSlotId = "atm-cash";

    /// <summary>Entity spawned when cash is withdrawn. Must be a <c>Credit</c> stack.</summary>
    [DataField]
    public EntProtoId CashPrototype = "SpaceCash";

    [DataField]
    public SoundSpecifier? WithdrawSound = new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg");

    [DataField]
    public SoundSpecifier? DepositSound = new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg");

    [DataField]
    public SoundSpecifier? DenySound = new SoundPathSpecifier("/Audio/Machines/buzz-two.ogg");

    /// <summary>Last message shown in the UI (locale id already resolved).</summary>
    [ViewVariables]
    public string Message = string.Empty;

    /// <summary>Result of the last "Verify holder" check, or null if none was run for the current card.</summary>
    [ViewVariables]
    public AtmVerifyResult? LastVerify;
}

[Serializable, NetSerializable]
public enum AtmUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public enum AtmVerifyResult : byte
{
    /// <summary>The card has no fingerprint on record or the user has none; cannot verify.</summary>
    Unknown,
    Match,
    Mismatch,
}

[Serializable, NetSerializable]
public sealed class AtmBoundUserInterfaceState(
    bool hasCard,
    string? ownerName,
    long balance,
    int cashInSlot,
    int withdrawRemaining,
    float feePercent,
    bool fingerprintRequired,
    AtmVerifyResult? verify,
    string message)
    : BoundUserInterfaceState
{
    public readonly bool HasCard = hasCard;
    public readonly string? OwnerName = ownerName;
    public readonly long Balance = balance;
    public readonly int CashInSlot = cashInSlot;

    /// <summary>Credits the user may still withdraw this round, or -1 when unlimited.</summary>
    public readonly int WithdrawRemaining = withdrawRemaining;
    public readonly float FeePercent = feePercent;
    public readonly bool FingerprintRequired = fingerprintRequired;
    public readonly AtmVerifyResult? Verify = verify;
    public readonly string Message = message;
}

[Serializable, NetSerializable]
public sealed class AtmWithdrawMessage(int amount) : BoundUserInterfaceMessage
{
    public readonly int Amount = amount;
}

[Serializable, NetSerializable]
public sealed class AtmDepositMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class AtmVerifyHolderMessage : BoundUserInterfaceMessage;
