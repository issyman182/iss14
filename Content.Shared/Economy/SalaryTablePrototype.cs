// iss14: Economy
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Shared.Economy;

/// <summary>
/// Credits per hour paid for time spent in each job. Jobs that are not listed fall back to
/// the <c>economy.salary_default_per_hour</c> CVar.
/// </summary>
[Prototype]
public sealed partial class SalaryTablePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>Job prototype id → credits per hour.</summary>
    [DataField(required: true)]
    public Dictionary<ProtoId<JobPrototype>, int> Jobs = new();
}
