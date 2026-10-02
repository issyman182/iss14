// iss14: Economy
using Content.Shared.Cargo.Prototypes;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Economy;

/// <summary>What kind of account a payment party refers to.</summary>
[Serializable, NetSerializable]
public enum EconomyPartyKind : byte
{
    /// <summary>Money entering or leaving the economy: salaries, vending, fees, CentComm payouts.</summary>
    External,

    /// <summary>A player's persistent bank account.</summary>
    Player,

    /// <summary>One department account of a station (a cargo account: Service, Security...). Lives for one round.</summary>
    Department,

    /// <summary>The station's primary account (the Cargo account) — "the station pays".</summary>
    Station,
}

/// <summary>
/// One side of a payment. All money movement goes through <c>EconomySystem</c> with two of these, so a cash
/// register, the cargo console, a fine terminal or a vending machine never care who is paying: the user, their
/// department or the station.
/// </summary>
public readonly record struct EconomyParty
{
    public readonly EconomyPartyKind Kind;
    public readonly NetUserId? UserId;
    public readonly EntityUid? Station;
    public readonly ProtoId<CargoAccountPrototype>? Account;

    private EconomyParty(EconomyPartyKind kind, NetUserId? userId, EntityUid? station, ProtoId<CargoAccountPrototype>? account)
    {
        Kind = kind;
        UserId = userId;
        Station = station;
        Account = account;
    }

    public static readonly EconomyParty External = new(EconomyPartyKind.External, null, null, null);

    public static EconomyParty Player(NetUserId userId) => new(EconomyPartyKind.Player, userId, null, null);

    public static EconomyParty Department(EntityUid station, ProtoId<CargoAccountPrototype> account)
        => new(EconomyPartyKind.Department, null, station, account);

    public static EconomyParty StationBudget(EntityUid station) => new(EconomyPartyKind.Station, null, station, null);

    /// <summary>Stable id used in the transaction log: user guid, account prototype id or empty.</summary>
    public string LogId => Kind switch
    {
        EconomyPartyKind.Player => UserId?.ToString() ?? string.Empty,
        EconomyPartyKind.Department => Account?.Id ?? string.Empty,
        EconomyPartyKind.Station => "station",
        _ => string.Empty,
    };

    public string LogKind => Kind switch
    {
        EconomyPartyKind.Player => "player",
        EconomyPartyKind.Department => "department",
        EconomyPartyKind.Station => "station",
        _ => "external",
    };

    public override string ToString() => Kind == EconomyPartyKind.External ? "external" : $"{LogKind}:{LogId}";
}

/// <summary>Why a payment failed, for user-facing popups.</summary>
public enum EconomyPaymentError : byte
{
    None,
    InvalidAmount,
    NoAccount,
    InsufficientFunds,
    AccountFull,
    NoAccess,
    FingerprintMismatch,
}
