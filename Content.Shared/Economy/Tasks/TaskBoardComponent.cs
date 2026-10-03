// iss14: Economy
using Content.Shared.Cargo.Prototypes;
using Content.Shared.Contraband;
using Content.Shared.Security;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Economy.Tasks;

/// <summary>
/// A station task board. One board serves every department: the ID card of the person using it decides which
/// departments' tasks they see (members of several departments, and the Captain, pick one in the UI). Tasks are
/// generated per department from <see cref="DepartmentTaskTemplatePrototype"/>s or written by department heads
/// (contracts). Members claim them; manual tasks are confirmed by a head, delivery tasks complete when the goods
/// are handed in at the board, action tasks (healing, arrests, patrols) track themselves.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class TaskBoardComponent : Component
{
    [DataField]
    public List<DepartmentTask> Tasks = new();

    /// <summary>How many generated (template) tasks a department may have open at once.</summary>
    [DataField]
    public int MaxGenerated = 3;

    /// <summary>How many tasks (of any kind) a department may have on the board.</summary>
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

    /// <summary>Done when the claimer did something the game can detect (see <see cref="DepartmentTaskAction"/>).</summary>
    Action,
}

/// <summary>What an action task tracks. Progress counts towards <see cref="DepartmentTask.DeliverTarget"/>.</summary>
public enum DepartmentTaskAction : byte
{
    None,

    /// <summary>Heal damage on other people (any source: bruise packs, ointment, chems, surgery). Progress = points healed.</summary>
    HealDamage,

    /// <summary>Set someone's criminal record to <see cref="DepartmentTask.ActionStatus"/> at a records console. Progress = people.</summary>
    SetRecordStatus,

    /// <summary>Walk past a number of different station beacons (the names on the nav map). Progress = beacons.</summary>
    Patrol,

    /// <summary>Go to one specific station beacon, picked when the task is generated.</summary>
    Visit,
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

    /// <summary>The department (account) this task belongs to.</summary>
    [DataField]
    public ProtoId<CargoAccountPrototype> Department = "Service";

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

    /// <summary>Delivery, cleaning and action tasks: how much is needed.</summary>
    [DataField]
    public int DeliverTarget;

    [DataField]
    public int DeliverProgress;

    /// <summary>Who handed things in (gets the reward if nobody claimed the task).</summary>
    public NetUserId? LastDeliverer;

    // Action tasks
    [DataField]
    public DepartmentTaskAction Action;

    /// <summary><see cref="DepartmentTaskAction.HealDamage"/>: only this damage group counts (null = any).</summary>
    [DataField]
    public string? ActionDamageGroup;

    /// <summary><see cref="DepartmentTaskAction.SetRecordStatus"/>: the status to set.</summary>
    [DataField]
    public SecurityStatus? ActionStatus;

    /// <summary><see cref="DepartmentTaskAction.SetRecordStatus"/>: the record must have had this status before (null = any).</summary>
    [DataField]
    public SecurityStatus? ActionFromStatus;

    /// <summary><see cref="DepartmentTaskAction.Visit"/>: name of the beacon to reach.</summary>
    [DataField]
    public string? ActionBeacon;

    /// <summary><see cref="DepartmentTaskAction.Patrol"/>: beacons already counted. Server only.</summary>
    [NonSerialized]
    public HashSet<string> VisitedBeacons = new();
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

    /// <summary>Delivery, cleaning and action tasks: the required amount is rolled between these.</summary>
    [DataField]
    public int DeliverMin = 1;

    [DataField]
    public int DeliverMax = 1;

    /// <summary>For <see cref="DepartmentTaskKind.Action"/> tasks: what to track.</summary>
    [DataField]
    public DepartmentTaskAction Action;

    /// <summary>HealDamage: only this damage group counts (e.g. <c>Brute</c>, <c>Burn</c>); null = any.</summary>
    [DataField]
    public string? ActionDamageGroup;

    /// <summary>SetRecordStatus: the status that has to be set.</summary>
    [DataField]
    public SecurityStatus? ActionStatus;

    /// <summary>SetRecordStatus: the record must have had this status before; null = any.</summary>
    [DataField]
    public SecurityStatus? ActionFromStatus;

    /// <summary>Relative pick weight.</summary>
    [DataField]
    public float Weight = 1f;
}

[Serializable, NetSerializable]
public enum TaskBoardUiKey : byte
{
    Key,
}

/// <summary>A department the viewer belongs to, as the UI shows it.</summary>
[Serializable, NetSerializable]
public sealed class TaskBoardDepartment(string id, string name, int balance, bool isHead)
{
    public readonly string Id = id;
    public readonly string Name = name;
    public readonly int Balance = balance;

    /// <summary>Whether the viewer may confirm, remove and write tasks of this department.</summary>
    public readonly bool IsHead = isHead;
}

[Serializable, NetSerializable]
public sealed class TaskBoardBuiState : BoundUserInterfaceState
{
    /// <summary>All tasks on the board; the client shows those of the viewer's departments.</summary>
    public List<DepartmentTask> Tasks = new();

    /// <summary>
    /// Per viewer (player entity): the departments they belong to. A viewer with an empty list, or none at all,
    /// is shown nothing.
    /// </summary>
    public Dictionary<NetEntity, List<TaskBoardDepartment>> Viewers = new();

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

/// <summary>Head: write a contract for one of their departments, paid from its account.</summary>
[Serializable, NetSerializable]
public sealed class TaskBoardCreateMessage(string department, string title, string description, int reward) : BoundUserInterfaceMessage
{
    public readonly string Department = department;
    public readonly string Title = title;
    public readonly string Description = description;
    public readonly int Reward = reward;
}
