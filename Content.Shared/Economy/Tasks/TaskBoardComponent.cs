// iss14: Economy
using Content.Shared.Cargo.Prototypes;
using Content.Shared.Contraband;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Economy.Tasks;

/// <summary>
/// A department task board. Tasks are generated from <see cref="DepartmentTaskTemplatePrototype"/>s or written by
/// department heads (contracts). Department members claim them; manual tasks are confirmed by a head, delivery
/// tasks complete themselves when the goods are handed in at the board. Rewards come from the department account
/// or, for CentComm tasks, from outside.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class TaskBoardComponent : Component
{
    [DataField]
    public ProtoId<CargoAccountPrototype> Account = "Service";

    [DataField]
    public List<DepartmentTask> Tasks = new();

    /// <summary>How many generated (template) tasks may be open at once.</summary>
    [DataField]
    public int MaxGenerated = 3;

    [DataField]
    public int MaxTasks = 12;

    [DataField]
    public int MaxReward = 50_000;

    [DataField]
    public uint MaxTextLength = 200;

    [DataField]
    public SoundSpecifier? CompleteSound = new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg");

    [DataField]
    public SoundSpecifier? DenySound = new SoundPathSpecifier("/Audio/Machines/buzz-two.ogg");

    [ViewVariables]
    public TimeSpan NextGeneration;

    [ViewVariables]
    public uint NextTaskId = 1;

    [ViewVariables]
    public string Message = string.Empty;
}

public enum DepartmentTaskKind : byte
{
    /// <summary>Done when a department head confirms it.</summary>
    Manual,

    /// <summary>Done when the required items were handed in at the board.</summary>
    Delivery,

    /// <summary>
    /// Done when the claimer has cleaned enough: mopped puddles (real dirt only) and scrubbed graffiti/dirt decals.
    /// Progress uses the delivery counters.
    /// </summary>
    Cleaning,
}

public enum DepartmentTaskPayer : byte
{
    Department,
    CentComm,
}

public enum DepartmentTaskStatus : byte
{
    Open,
    Claimed,
    Completed,
}

[Serializable, NetSerializable, DataDefinition]
public sealed partial class DepartmentTask
{
    [DataField]
    public uint Id;

    [DataField]
    public string Title = string.Empty;

    [DataField]
    public string Description = string.Empty;

    [DataField]
    public int Reward;

    [DataField]
    public DepartmentTaskKind Kind;

    [DataField]
    public DepartmentTaskPayer Payer;

    [DataField]
    public DepartmentTaskStatus Status;

    [DataField]
    public string Issuer = string.Empty;

    /// <summary>True for tasks generated from a template (count against <see cref="TaskBoardComponent.MaxGenerated"/>).</summary>
    [DataField]
    public bool Generated;

    [DataField]
    public string? ClaimedBy;

    public NetUserId? ClaimedUser;

    [DataField]
    public TimeSpan Created;

    // Delivery tasks
    [DataField]
    public EntProtoId? DeliverPrototype;

    [DataField]
    public string? DeliverTag;

    /// <summary>Component name a delivered item must have (e.g. <c>Contraband</c>).</summary>
    [DataField]
    public string? DeliverComponent;

    /// <summary>With <see cref="DeliverComponent"/> = Contraband: accepted severities (empty = any).</summary>
    [DataField]
    public List<ProtoId<ContrabandSeverityPrototype>> DeliverContrabandSeverities = new();

    [DataField]
    public int DeliverTarget;

    [DataField]
    public int DeliverProgress;

    /// <summary>Who handed things in (gets the reward if nobody claimed the task).</summary>
    public NetUserId? LastDeliverer;
}

/// <summary>A task the board can generate for a department.</summary>
[Prototype]
public sealed partial class DepartmentTaskTemplatePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public ProtoId<CargoAccountPrototype> Department;

    [DataField(required: true)]
    public LocId Title;

    [DataField(required: true)]
    public LocId Description;

    [DataField]
    public int RewardMin = 100;

    [DataField]
    public int RewardMax = 300;

    [DataField]
    public DepartmentTaskKind Kind = DepartmentTaskKind.Manual;

    [DataField]
    public DepartmentTaskPayer Payer = DepartmentTaskPayer.Department;

    [DataField]
    public EntProtoId? DeliverPrototype;

    [DataField]
    public string? DeliverTag;

    /// <summary>Component name a delivered item must have (e.g. <c>Contraband</c>).</summary>
    [DataField]
    public string? DeliverComponent;

    /// <summary>With <see cref="DeliverComponent"/> = Contraband: accepted severities (empty = any).</summary>
    [DataField]
    public List<ProtoId<ContrabandSeverityPrototype>> DeliverContrabandSeverities = new();

    [DataField]
    public int DeliverMin = 1;

    [DataField]
    public int DeliverMax = 1;

    /// <summary>Relative pick weight.</summary>
    [DataField]
    public float Weight = 1f;
}

[Serializable, NetSerializable]
public enum TaskBoardUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class TaskBoardBuiState : BoundUserInterfaceState
{
    public List<DepartmentTask> Tasks = new();
    public string Department = string.Empty;
    public int DepartmentBalance;
    public bool IsMember;
    public bool IsHead;
    public string Message = string.Empty;
}

[Serializable, NetSerializable]
public sealed class TaskBoardClaimMessage(uint task) : BoundUserInterfaceMessage
{
    public readonly uint Task = task;
}

[Serializable, NetSerializable]
public sealed class TaskBoardAbandonMessage(uint task) : BoundUserInterfaceMessage
{
    public readonly uint Task = task;
}

/// <summary>Head: confirm a manual task as done (pays the claimer).</summary>
[Serializable, NetSerializable]
public sealed class TaskBoardConfirmMessage(uint task) : BoundUserInterfaceMessage
{
    public readonly uint Task = task;
}

/// <summary>Head: remove a task.</summary>
[Serializable, NetSerializable]
public sealed class TaskBoardCancelMessage(uint task) : BoundUserInterfaceMessage
{
    public readonly uint Task = task;
}

/// <summary>Head: write a contract paid from the department account.</summary>
[Serializable, NetSerializable]
public sealed class TaskBoardCreateMessage(string title, string description, int reward) : BoundUserInterfaceMessage
{
    public readonly string Title = title;
    public readonly string Description = description;
    public readonly int Reward = reward;
}
