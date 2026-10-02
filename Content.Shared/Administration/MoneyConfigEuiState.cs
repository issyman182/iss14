// iss14: Economy
using Content.Shared.Eui;
using Robust.Shared.Serialization;

namespace Content.Shared.Administration;

/// <summary>How a config value is edited in the panel.</summary>
[Serializable, NetSerializable]
public enum MoneyConfigKind : byte
{
    Toggle,
    Integer,
    Decimal,
    Percent,
}

/// <summary>One editable <c>economy.*</c> value.</summary>
[Serializable, NetSerializable]
public sealed class MoneyConfigEntry(string key, string section, string label, MoneyConfigKind kind, string value)
{
    /// <summary>CVar name, e.g. <c>economy.salary_enabled</c>.</summary>
    public readonly string Key = key;

    /// <summary>Localized section heading the entry belongs to.</summary>
    public readonly string Section = section;

    /// <summary>Localized label.</summary>
    public readonly string Label = label;

    public readonly MoneyConfigKind Kind = kind;

    /// <summary>Current value, invariant-culture formatted.</summary>
    public readonly string Value = value;
}

/// <summary>One job's row in the salary table.</summary>
[Serializable, NetSerializable]
public sealed class MoneySalaryRow(string jobId, string jobName, string department, int salary, int defaultSalary, bool overridden)
{
    public readonly string JobId = jobId;
    public readonly string JobName = jobName;
    public readonly string Department = department;

    /// <summary>Effective per-round base salary.</summary>
    public readonly int Salary = salary;

    /// <summary>What the salary table prototype (or the default CVar) says.</summary>
    public readonly int DefaultSalary = defaultSalary;

    public readonly bool Overridden = overridden;
}

/// <summary>State for the economy config admin EUI (<c>moneyconfig</c>).</summary>
[Serializable, NetSerializable]
public sealed class MoneyConfigEuiState(bool canEdit, List<MoneyConfigEntry> entries, List<MoneySalaryRow> salaries, string status)
    : EuiStateBase
{
    public readonly bool CanEdit = canEdit;
    public readonly List<MoneyConfigEntry> Entries = entries;
    public readonly List<MoneySalaryRow> Salaries = salaries;
    public readonly string Status = status;
}

/// <summary>Sets one economy CVar. The server parses <see cref="Value"/> according to the entry's kind.</summary>
[Serializable, NetSerializable]
public sealed class MoneyConfigSetMessage(string key, string value) : EuiMessageBase
{
    public readonly string Key = key;
    public readonly string Value = value;
}

/// <summary>Sets a job's per-round base salary override; an empty value resets to the prototype default.</summary>
[Serializable, NetSerializable]
public sealed class MoneyConfigSetSalaryMessage(string jobId, string value) : EuiMessageBase
{
    public readonly string JobId = jobId;
    public readonly string Value = value;
}
