// iss14: Economy
using Content.Shared.Access;
using Content.Shared.Cargo.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared.Economy;

/// <summary>
/// Economy metadata for a department account (<see cref="CargoAccountPrototype"/>): who may spend it and how much
/// it starts with. The prototype id must equal the cargo account id.
/// </summary>
[Prototype]
public sealed partial class EconomyDepartmentPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    /// Access levels that may spend from the department account (department heads). Anyone with one of these may
    /// pay with the department account at a cash register, cargo console or task board.
    /// </summary>
    [DataField]
    public List<ProtoId<AccessLevelPrototype>> HeadAccess = new();

    /// <summary>
    /// Access levels that may view the account balance and department tasks (the department's own staff).
    /// </summary>
    [DataField]
    public List<ProtoId<AccessLevelPrototype>> MemberAccess = new();
}
