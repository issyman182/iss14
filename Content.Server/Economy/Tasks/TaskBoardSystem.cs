// iss14: Economy
using System.Linq;
using Content.Server.Administration.Logs;
using Content.Server.Chat.Systems;
using Content.Server.GameTicking;
using Content.Server.Station.Systems;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Database;
using Content.Shared.Economy;
using Content.Shared.Economy.Tasks;
using Content.Shared.GameTicking;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Content.Shared.Tag;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.Economy.Tasks;

/// <summary>Department task boards: generation, claiming, delivery hand-ins, confirmation and payout.</summary>
public sealed partial class TaskBoardSystem : EntitySystem
{
    [Dependency] private EconomySystem _economy = default!;
    [Dependency] private ServerStationSystem _station = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private TagSystem _tag = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IAdminLogManager _adminLog = default!;
    [Dependency] private ServerGameTicker _ticker = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<TaskBoardComponent, BeforeActivatableUIOpenEvent>((e, c, a) => UpdateUi((e, c), a.User));
        SubscribeLocalEvent<TaskBoardComponent, TaskBoardClaimMessage>(OnClaim);
        SubscribeLocalEvent<TaskBoardComponent, TaskBoardAbandonMessage>(OnAbandon);
        SubscribeLocalEvent<TaskBoardComponent, TaskBoardConfirmMessage>(OnConfirm);
        SubscribeLocalEvent<TaskBoardComponent, TaskBoardCancelMessage>(OnCancel);
        SubscribeLocalEvent<TaskBoardComponent, TaskBoardCreateMessage>(OnCreate);
        SubscribeLocalEvent<TaskBoardComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        var query = EntityQueryEnumerator<TaskBoardComponent>();
        while (query.MoveNext(out _, out var board))
        {
            board.Tasks.Clear();
            board.NextTaskId = 1;
            board.NextGeneration = TimeSpan.Zero;
        }
    }

    #region Generation

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_ticker.RunLevel != GameRunLevel.InRound || !_cfg.GetCVar(CCVars.EconomyTasksEnabled))
            return;

        var interval = TimeSpan.FromSeconds(Math.Max(30, _cfg.GetCVar(CCVars.EconomyTaskIntervalSeconds)));
        var query = EntityQueryEnumerator<TaskBoardComponent>();
        while (query.MoveNext(out var uid, out var board))
        {
            if (_timing.CurTime < board.NextGeneration)
                continue;

            // First task soon after the board exists, then every interval (randomized a little).
            board.NextGeneration = _timing.CurTime + interval * _random.NextFloat(0.8f, 1.2f);
            if (TryGenerate((uid, board), out var task))
            {
                UpdateUi((uid, board), null);
                _chat.TrySendInGameICMessage(uid, Loc.GetString("task-board-announce", ("title", task.Title), ("reward", task.Reward)),
                    InGameICChatType.Speak, ChatTransmitRange.Normal, hideLog: true);
            }
        }
    }

    public bool TryGenerate(Entity<TaskBoardComponent> ent, out DepartmentTask task)
    {
        task = default!;
        var board = ent.Comp;

        if (board.Tasks.Count(t => t.Generated && t.Status != DepartmentTaskStatus.Completed) >= board.MaxGenerated
            || board.Tasks.Count >= board.MaxTasks)
            return false;

        var templates = ProtoMan.EnumeratePrototypes<DepartmentTaskTemplatePrototype>()
            .Where(t => t.Department == board.Account)
            .Where(t => board.Tasks.All(existing => existing.Status == DepartmentTaskStatus.Completed || existing.Title != Loc.GetString(t.Title)))
            .ToList();

        if (templates.Count == 0)
            return false;

        var total = templates.Sum(t => Math.Max(0f, t.Weight));
        var roll = _random.NextFloat() * total;
        DepartmentTaskTemplatePrototype? chosen = null;
        foreach (var template in templates)
        {
            roll -= Math.Max(0f, template.Weight);
            if (roll <= 0)
            {
                chosen = template;
                break;
            }
        }

        chosen ??= templates[^1];

        var count = _random.Next(Math.Max(1, chosen.DeliverMin), Math.Max(chosen.DeliverMin, chosen.DeliverMax) + 1);
        var multiplier = Math.Max(0f, _cfg.GetCVar(CCVars.EconomyTaskRewardMultiplier));
        var reward = (int) Math.Round(_random.Next(chosen.RewardMin, chosen.RewardMax + 1) * multiplier);

        task = new DepartmentTask
        {
            Id = board.NextTaskId++,
            Title = Loc.GetString(chosen.Title, ("count", count)),
            Description = Loc.GetString(chosen.Description, ("count", count)),
            Reward = reward,
            Kind = chosen.Kind,
            Payer = chosen.Payer,
            Issuer = Loc.GetString(chosen.Payer == DepartmentTaskPayer.CentComm ? "task-board-issuer-centcomm" : "task-board-issuer-department"),
            Generated = true,
            Created = _ticker.RoundDuration(),
            DeliverPrototype = chosen.DeliverPrototype,
            DeliverTag = chosen.DeliverTag,
            DeliverTarget = chosen.Kind == DepartmentTaskKind.Delivery ? count : 0,
        };

        board.Tasks.Add(task);
        return true;
    }

    #endregion

    #region UI

    private void UpdateUi(Entity<TaskBoardComponent> ent, EntityUid? user)
    {
        var station = _station.GetOwningStation(ent);
        var actor = user ?? _ui.GetActors(ent.Owner, TaskBoardUiKey.Key).FirstOrDefault();
        var state = new TaskBoardBuiState
        {
            Tasks = ent.Comp.Tasks.ToList(),
            Department = _economy.Describe(EconomyParty.Department(station ?? EntityUid.Invalid, ent.Comp.Account)),
            DepartmentBalance = station != null ? (int) _economy.GetBalance(EconomyParty.Department(station.Value, ent.Comp.Account)) : 0,
            IsMember = actor != default && _economy.IsDepartmentMember(actor, ent.Comp.Account),
            IsHead = actor != default && _economy.CanSpendDepartment(actor, ent.Comp.Account),
            Message = ent.Comp.Message,
        };
        _ui.SetUiState(ent.Owner, TaskBoardUiKey.Key, state);
    }

    private void Fail(Entity<TaskBoardComponent> ent, EntityUid actor, string message)
    {
        ent.Comp.Message = message;
        _audio.PlayPvs(ent.Comp.DenySound, ent);
        UpdateUi(ent, actor);
    }

    private void Ok(Entity<TaskBoardComponent> ent, EntityUid actor, string message)
    {
        ent.Comp.Message = message;
        UpdateUi(ent, actor);
    }

    private DepartmentTask? Find(Entity<TaskBoardComponent> ent, uint id) => ent.Comp.Tasks.FirstOrDefault(t => t.Id == id);

    private void OnClaim(Entity<TaskBoardComponent> ent, ref TaskBoardClaimMessage args)
    {
        if (!_economy.IsDepartmentMember(args.Actor, ent.Comp.Account))
        {
            Fail(ent, args.Actor, Loc.GetString("task-board-not-member"));
            return;
        }

        if (Find(ent, args.Task) is not { Status: DepartmentTaskStatus.Open } task)
        {
            Fail(ent, args.Actor, Loc.GetString("task-board-not-open"));
            return;
        }

        task.Status = DepartmentTaskStatus.Claimed;
        task.ClaimedBy = Name(args.Actor);
        task.ClaimedUser = _players.TryGetSessionByEntity(args.Actor, out var session) ? session.UserId : null;
        Ok(ent, args.Actor, Loc.GetString("task-board-claimed", ("title", task.Title)));
    }

    private void OnAbandon(Entity<TaskBoardComponent> ent, ref TaskBoardAbandonMessage args)
    {
        if (Find(ent, args.Task) is not { Status: DepartmentTaskStatus.Claimed } task)
            return;

        var isClaimer = _players.TryGetSessionByEntity(args.Actor, out var session) && session.UserId == task.ClaimedUser;
        if (!isClaimer && !_economy.CanSpendDepartment(args.Actor, ent.Comp.Account))
        {
            Fail(ent, args.Actor, Loc.GetString("task-board-not-yours"));
            return;
        }

        task.Status = DepartmentTaskStatus.Open;
        task.ClaimedBy = null;
        task.ClaimedUser = null;
        Ok(ent, args.Actor, Loc.GetString("task-board-abandoned", ("title", task.Title)));
    }

    private void OnConfirm(Entity<TaskBoardComponent> ent, ref TaskBoardConfirmMessage args)
    {
        if (!_economy.CanSpendDepartment(args.Actor, ent.Comp.Account))
        {
            Fail(ent, args.Actor, Loc.GetString("task-board-not-head"));
            return;
        }

        if (Find(ent, args.Task) is not { Status: DepartmentTaskStatus.Claimed, Kind: DepartmentTaskKind.Manual } task)
        {
            Fail(ent, args.Actor, Loc.GetString("task-board-cannot-confirm"));
            return;
        }

        if (task.ClaimedUser is not { } worker)
        {
            Fail(ent, args.Actor, Loc.GetString("task-board-no-worker"));
            return;
        }

        Complete(ent, task, worker, args.Actor);
    }

    private void OnCancel(Entity<TaskBoardComponent> ent, ref TaskBoardCancelMessage args)
    {
        if (!_economy.CanSpendDepartment(args.Actor, ent.Comp.Account))
        {
            Fail(ent, args.Actor, Loc.GetString("task-board-not-head"));
            return;
        }

        if (Find(ent, args.Task) is not { } task)
            return;

        ent.Comp.Tasks.Remove(task);
        _adminLog.Add(LogType.Economy, LogImpact.Low, $"{ToPrettyString(args.Actor):player} removed task '{task.Title}' from {ToPrettyString(ent)}");
        Ok(ent, args.Actor, Loc.GetString("task-board-cancelled", ("title", task.Title)));
    }

    private void OnCreate(Entity<TaskBoardComponent> ent, ref TaskBoardCreateMessage args)
    {
        if (!_economy.CanSpendDepartment(args.Actor, ent.Comp.Account))
        {
            Fail(ent, args.Actor, Loc.GetString("task-board-not-head"));
            return;
        }

        if (ent.Comp.Tasks.Count >= ent.Comp.MaxTasks)
        {
            Fail(ent, args.Actor, Loc.GetString("task-board-full"));
            return;
        }

        var title = args.Title.Trim();
        var description = args.Description.Trim();
        var reward = Math.Clamp(args.Reward, 0, ent.Comp.MaxReward);
        if (title.Length == 0 || reward <= 0)
        {
            Fail(ent, args.Actor, Loc.GetString("task-board-invalid"));
            return;
        }

        if (title.Length > ent.Comp.MaxTextLength)
            title = title[..(int) ent.Comp.MaxTextLength];
        if (description.Length > ent.Comp.MaxTextLength)
            description = description[..(int) ent.Comp.MaxTextLength];

        var station = _station.GetOwningStation(ent);
        if (station == null || _economy.GetBalance(EconomyParty.Department(station.Value, ent.Comp.Account)) < reward)
        {
            Fail(ent, args.Actor, Loc.GetString("task-board-department-broke"));
            return;
        }

        var task = new DepartmentTask
        {
            Id = ent.Comp.NextTaskId++,
            Title = title,
            Description = description,
            Reward = reward,
            Kind = DepartmentTaskKind.Manual,
            Payer = DepartmentTaskPayer.Department,
            Issuer = Name(args.Actor),
            Created = _ticker.RoundDuration(),
        };
        ent.Comp.Tasks.Add(task);
        _adminLog.Add(LogType.Economy, LogImpact.Low, $"{ToPrettyString(args.Actor):player} posted contract '{title}' ({reward}) on {ToPrettyString(ent)}");
        Ok(ent, args.Actor, Loc.GetString("task-board-created", ("title", title)));
    }

    #endregion

    #region Delivery

    private void OnInteractUsing(Entity<TaskBoardComponent> ent, ref InteractUsingEvent args)
    {
        if (args.Handled)
            return;

        // Find a delivery task that wants this item; claimed-by-user first, then any open/claimed one.
        var used = args.Used;
        var candidates = ent.Comp.Tasks
            .Where(t => t.Kind == DepartmentTaskKind.Delivery && t.Status != DepartmentTaskStatus.Completed && Matches(t, used))
            .ToList();

        if (candidates.Count == 0)
            return;

        _players.TryGetSessionByEntity(args.User, out var session);
        var task = candidates.FirstOrDefault(t => session != null && t.ClaimedUser == session.UserId) ?? candidates[0];

        if (task.Status == DepartmentTaskStatus.Open && !_economy.IsDepartmentMember(args.User, ent.Comp.Account))
        {
            _popup.PopupEntity(Loc.GetString("task-board-not-member"), ent, args.User, PopupType.SmallCaution);
            args.Handled = true;
            return;
        }

        args.Handled = true;

        var needed = task.DeliverTarget - task.DeliverProgress;
        var count = TryComp<StackComponent>(args.Used, out var stack) ? stack.Count : 1;
        var take = Math.Min(needed, count);

        if (stack != null && take < stack.Count)
            EntityManager.System<Content.Server.Stack.StackSystem>().SetCount(args.Used, stack.Count - take);
        else
            QueueDel(args.Used);

        task.DeliverProgress += take;
        task.LastDeliverer = session?.UserId;
        _popup.PopupEntity(Loc.GetString("task-board-delivered", ("progress", task.DeliverProgress), ("target", task.DeliverTarget)), ent, args.User);

        if (task.DeliverProgress >= task.DeliverTarget)
        {
            var worker = task.ClaimedUser ?? task.LastDeliverer;
            if (worker != null)
                Complete(ent, task, worker.Value, args.User);
        }

        UpdateUi(ent, args.User);
    }

    private bool Matches(DepartmentTask task, EntityUid item)
    {
        if (task.DeliverPrototype is { } proto && MetaData(item).EntityPrototype?.ID == proto.Id)
            return true;

        return task.DeliverTag != null && _tag.HasTag(item, task.DeliverTag);
    }

    #endregion

    private void Complete(Entity<TaskBoardComponent> ent, DepartmentTask task, Robust.Shared.Network.NetUserId worker, EntityUid actor)
    {
        var station = _station.GetOwningStation(ent);
        var payee = EconomyParty.Player(worker);
        var reason = $"task '{task.Title}' on {ToPrettyString(ent)}";

        if (task.Payer == DepartmentTaskPayer.Department)
        {
            var error = EconomyPaymentError.NoAccount;
            if (station == null || !_economy.TryTransfer(EconomyParty.Department(station.Value, ent.Comp.Account), payee, task.Reward, reason, out error))
            {
                // Offline worker or empty department account: keep the task so it can be paid later.
                if (station != null && error == EconomyPaymentError.NoAccount)
                    _economy.Pay(payee, task.Reward, reason);
                else
                {
                    Fail(ent, actor, _economy.ErrorText(error));
                    return;
                }
            }
        }
        else
        {
            _economy.Pay(payee, task.Reward, reason);
        }

        task.Status = DepartmentTaskStatus.Completed;
        _audio.PlayPvs(ent.Comp.CompleteSound, ent);
        _adminLog.Add(LogType.Economy, LogImpact.Low, $"Task '{task.Title}' on {ToPrettyString(ent)} completed by {worker}, paid {task.Reward}");

        if (_players.TryGetSessionById(worker, out var session) && session.AttachedEntity is { } mob)
            _popup.PopupEntity(Loc.GetString("task-board-reward", ("reward", task.Reward), ("title", task.Title)), mob, mob, PopupType.Medium);

        // Keep a short history, drop the oldest completed ones.
        var completed = ent.Comp.Tasks.Where(t => t.Status == DepartmentTaskStatus.Completed).ToList();
        while (completed.Count > 4)
        {
            ent.Comp.Tasks.Remove(completed[0]);
            completed.RemoveAt(0);
        }

        Ok(ent, actor, Loc.GetString("task-board-completed", ("title", task.Title), ("reward", task.Reward)));
    }
}
