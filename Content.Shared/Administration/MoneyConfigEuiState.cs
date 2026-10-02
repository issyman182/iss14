// iss14: Economy
using Content.Shared.Eui;
using Robust.Shared.Serialization;

namespace Content.Shared.Administration;

/// <summary>Which economy config value a panel field edits.</summary>
public enum MoneyConfigField : byte
{
    MaxBalance,
    SalaryEnabled,
    SalaryDefaultPerHour,
    SalaryMultiplier,
    AtmRequireFingerprint,
    AtmWithdrawLimit,
    AtmFeePercent,
    VendingPrices,
    VendingPriceMultiplier,
    ChipRate,
}

/// <summary>State for the economy config admin EUI (<c>moneyconfig</c>).</summary>
[Serializable, NetSerializable]
public sealed class MoneyConfigEuiState(
    bool canEdit,
    long maxBalance,
    bool salaryEnabled,
    int salaryDefaultPerHour,
    float salaryMultiplier,
    bool atmRequireFingerprint,
    int atmWithdrawLimit,
    float atmFeePercent,
    bool vendingPrices,
    float vendingPriceMultiplier,
    int chipRate,
    string status)
    : EuiStateBase
{
    public readonly bool CanEdit = canEdit;
    public readonly long MaxBalance = maxBalance;
    public readonly bool SalaryEnabled = salaryEnabled;
    public readonly int SalaryDefaultPerHour = salaryDefaultPerHour;
    public readonly float SalaryMultiplier = salaryMultiplier;
    public readonly bool AtmRequireFingerprint = atmRequireFingerprint;
    public readonly int AtmWithdrawLimit = atmWithdrawLimit;
    public readonly float AtmFeePercent = atmFeePercent;
    public readonly bool VendingPrices = vendingPrices;
    public readonly float VendingPriceMultiplier = vendingPriceMultiplier;
    public readonly int ChipRate = chipRate;
    public readonly string Status = status;
}

/// <summary>Sets one economy config value. The server parses <see cref="Value"/> per <see cref="Field"/>.</summary>
[Serializable, NetSerializable]
public sealed class MoneyConfigSetMessage(MoneyConfigField field, string value) : EuiMessageBase
{
    public readonly MoneyConfigField Field = field;
    public readonly string Value = value;
}
