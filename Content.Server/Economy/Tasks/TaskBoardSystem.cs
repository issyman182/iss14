// iss14: Economy
using System.Linq;
using Content.Server.Administration.Logs;
using Content.Server.Chat.Systems;
using Content.Server.CriminalRecords.Systems;
using Content.Server.GameTicking;
using Content.Server.Station.Systems;
using Content.Shared.Body.Part;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Contraband;
using Content.Shared.Medical.Healing;
using Content.Shared.Database;
using Content.Shared.Economy;
using Content.Shared.Economy.Tasks;
using Content.Shared.GameTicking;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Components;
using Content.Shared.Pinpointer;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Content.Shared.Station.Components;
using Content.Shared.Tag;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.Economy.Tasks;

/// <summary>
/// Station task boards: per-department generation, claiming, delivery hand-ins, action tracking (healing,
/// records, patrols), confirmation and payout. One board serves every department; the viewer's ID decides what
/// they see.
/// </summary>
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
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private Content.Shared.Access.Systems.AccessReaderSystem _access = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private IComponentFactory _factory = default!;

    /// <summary>How close (tiles) a patrol has to get to a beacon for it to count.</summary>
    private const float BeaconRange = 3f;

    private TimeSpan _nextPatrolCheck;

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
        SubscribeLocalEvent<PuddleMoppedEvent>(OnPuddleMopped);
        SubscribeLocalEvent<DecalsCleanedEvent>(OnDecalsCleaned);
        SubscribeLocalEvent<HealedByPlayerEvent>(OnHealed);
        SubscribeLocalEvent<CriminalStatusSetByOfficerEvent>(OnCriminalStatusSet);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        _creditedPuddles.Clear();
        var query = EntityQueryEnumerator<TaskBoardComponent>();
        while (query.MoveNext(out _, out var board))
        {
            board.Tasks.Clear();
            board.NextTaskId = 1;
            board.NextGeneration = TimeSpan.Zero;
        }
    }

    private IEnumerable<ProtoId<CargoAccountPrototype>> AllDepartments()
        => ProtoMan.EnumeratePrototypes<EconomyDepartmentPrototype>().Select(d => new ProtoId<CargoAccountPrototype>(d.ID)).OrderBy(d => d.Id);

    #region Generation

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_ticker.RunLevel != GameRunLevel.InRound)
            return;

        if (_timing.CurTime >= _nextPatrolCheck)
        {
            _nextPatrolCheck = _timing.CurTime + TimeSpan.FromSeconds(2);
            CheckPatrols();
        }

        if (!_cfg.GetCVar(CCVars.EconomyTasksEnabled))
            return;

        var interval = TimeSpan.FromSeconds(Math.Max(30, _cfg.GetCVar(CCVars.EconomyTaskIntervalSeconds)));
        var query = EntityQueryEnumerator<TaskBoardComponent>();
        while (query.MoveNext(out var uid, out var board))
        {
            if (_timing.CurTime < board.NextGeneration)
                continue;

            // First task soon after the board exists, then every interval (randomized a little). Every tick of
            // the interval, one department gets a task; departments take turns so the board fills evenly.
            board.NextGeneration = _timing.CurTime + interval * _random.NextFloat(0.8f, 1.2f);

            var departments = AllDepartments().ToList();
            _random.Shuffle(departments);
            foreach (var department in departments)
            {
                if (!TryGenerate((uid, board), department, out var task))
                    continue;

                UpdateUi((uid, board), null);
                _chat.TrySendInGameICMessage(uid, Loc.GetString("task-board-announce", ("title", task.Title), ("reward", task.Reward)),
                    InGameICChatType.Speak, ChatTransmitRange.Normal, hideLog: true);
                break;
            }
        }
    }

    public bool TryGenerate(Entity<TaskBoardComponent> ent, ProtoId<CargoAccountPrototype> department, out DepartmentTask task)
    {
        task = default!;
        var board = ent.Comp;
        var existing = board.Tasks.Where(t => t.Department == department).ToList();

        if (existing.Count(t => t.Generated && t.Status != DepartmentTaskStatus.Completed) >= board.MaxGenerated
            || existing.Count >= board.MaxTasks)
            return false;

        var templates = ProtoMan.EnumeratePrototypes<DepartmentTaskTemplatePrototype>()
            .Where(t => t.Department == department)
            .Where(t => existing.All(e => e.Status == DepartmentTaskStatus.Completed || e.Title != Loc.GetString(t.Title)))
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

        // A visit task needs a destination: a random station beacon that no other open visit task uses.
        string? beacon = null;
        if (chosen.Kind == DepartmentTaskKind.Action && chosen.Action == DepartmentTaskAction.Visit)
        {
            var taken = board.Tasks.Where(t => t.Status != DepartmentTaskStatus.Completed && t.ActionBeacon != null).Select(t => t.ActionBeacon!).ToHashSet();
            var options = StationBeacons(ent).Where(b => !taken.Contains(b)).ToList();
            if (options.Count == 0)
                return false;

            beacon = _random.Pick(options);
        }

        var hasTarget = chosen.Kind is DepartmentTaskKind.Delivery or DepartmentTaskKind.Cleaning or DepartmentTaskKind.Action;
        task = new DepartmentTask
        {
            Id = board.NextTaskId++,
            Department = department,
            Title = Loc.GetString(chosen.Title, ("count", count), ("beacon", beacon ?? string.Empty)),
            Description = Loc.GetString(chosen.Description, ("count", count), ("beacon", beacon ?? string.Empty)),
            Reward = reward,
            Kind = chosen.Kind,
            Payer = chosen.Payer,
            Issuer = Loc.GetString(chosen.Payer == DepartmentTaskPayer.CentComm ? "task-board-issuer-centcomm" : "task-board-issuer-department"),
            Generated = true,
            Created = _ticker.RoundDuration(),
            DeliverPrototype = chosen.DeliverPrototype,
            DeliverTag = chosen.DeliverTag,
            DeliverComponent = chosen.DeliverComponent,
            DeliverContrabandSeverities = chosen.DeliverContrabandSeverities,
            DeliverTarget = hasTarget ? (chosen.Action == DepartmentTaskAction.Visit ? 1 : count) : 0,
            Action = chosen.Action,
            ActionDamageGroup = chosen.ActionDamageGroup,
            ActionStatus = chosen.ActionStatus,
            ActionFromStatus = chosen.ActionFromStatus,
            ActionBeacon = beacon,
        };

        board.Tasks.Add(task);
        return true;
    }

    /// <summary>Names of the enabled, anchored nav beacons on the board's station.</summary>
    private List<string> StationBeacons(Entity<TaskBoardComponent> ent)
    {
        var result = new HashSet<string>();
        if (_station.GetOwningStation(ent) is not { } station || !TryComp<StationDataComponent>(station, out var data))
            return result.ToList();

        var grids = new HashSet<EntityUid>(data.Grids);
        var query = EntityQueryEnumerator<NavMapBeaconComponent, TransformComponent, MetaDataComponent>();
        while (query.MoveNext(out _, out var beacon, out var xform, out var meta))
        {
            if (!beacon.Enabled || !xform.Anchored || xform.GridUid is not { } grid || !grids.Contains(grid))
                continue;

            var name = BeaconName(beacon, meta);
            if (name.Length > 0)
                result.Add(name);
        }

        return result.ToList();
    }

    private string BeaconName(NavMapBeaconComponent beacon, MetaDataComponent meta)
    {
        var name = beacon.Text;
        if (string.IsNullOrEmpty(name) && beacon.DefaultText != null)
            name = Loc.GetString(beacon.DefaultText);
        if (string.IsNullOrEmpty(name))
            name = meta.EntityName;
        return name ?? string.Empty;
    }

    #endregion

    #region UI

    /// <summary>Departments the viewer belongs to (the Captain: all), with their balances and head rights.</summary>
    private List<TaskBoardDepartment> DepartmentsFor(Entity<TaskBoardComponent> ent, EntityUid? user)
    {
        var result = new List<TaskBoardDepartment>();
        if (user == null)
            return result;

        var station = _station.GetOwningStation(ent);
        foreach (var department in AllDepartments())
        {
            if (!_economy.IsDepartmentMember(user.Value, department))
                continue;

            var party = EconomyParty.Department(station ?? EntityUid.Invalid, department);
            var balance = station != null ? (int) _economy.GetBalance(party) : 0;
            result.Add(new TaskBoardDepartment(department, _economy.Describe(party), balance, _economy.CanSpendDepartment(user.Value, department)));
        }

        return result;
    }

    private void UpdateUi(Entity<TaskBoardComponent> ent, EntityUid? user)
    {
        // The state is shared by everyone looking at the board; each viewer's departments are listed by viewer
        // and the client picks its own entry and filters the tasks accordingly.
        var actors = _ui.GetActors(ent.Owner, TaskBoardUiKey.Key).ToHashSet();
        if (user != null)
            actors.Add(user.Value);

        var state = new TaskBoardBuiState
        {
            Tasks = ent.Comp.Tasks.ToList(),
            Message = ent.Comp.Message,
        };
        foreach (var actor in actors)
            state.Viewers[GetNetEntity(actor)] = DepartmentsFor(ent, actor);

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
        if (Find(ent, args.Task) is not { Status: DepartmentTaskStatus.Open } task)
        {
            Fail(ent, args.Actor, Loc.GetString("task-board-not-open"));
            return;
        }

        if (!_economy.IsDepartmentMember(args.Actor, task.Department))
        {
            Fail(ent, args.Actor, Loc.GetString("task-board-not-member"));
            return;
        }

        task.Status = DepartmentTaskStatus.Claimed;
        task.ClaimedBy = Name(args.Actor);
        task.ClaimedUser = _players.TryGetSessionByEntity(args.Actor, out var session) ? session.UserId : null;
        task.VisitedBeacons.Clear();
        Ok(ent, args.Actor, Loc.GetString("task-board-claimed", ("title", task.Title)));
    }

    private void OnAbandon(Entity<TaskBoardComponent> ent, ref TaskBoardAbandonMessage args)
    {
        if (Find(ent, args.Task) is not { Status: DepartmentTaskStatus.Claimed } task)
            return;

        var isClaimer = _players.TryGetSessionByEntity(args.Actor, out var session) && session.UserId == task.ClaimedUser;
        if (!isClaimer && !_economy.CanSpendDepartment(args.Actor, task.Department))
        {
            Fail(ent, args.Actor, Loc.GetString("task-board-not-yours"));
            return;
        }

        task.Status = DepartmentTaskStatus.Open;
        task.ClaimedBy = null;
        task.ClaimedUser = null;
        if (task.Kind == DepartmentTaskKind.Action)
        {
            task.DeliverProgress = 0;
            task.VisitedBeacons.Clear();
        }

        Ok(ent, args.Actor, Loc.GetString("task-board-abandoned", ("title", task.Title)));
    }

    private void OnConfirm(Entity<TaskBoardComponent> ent, ref TaskBoardConfirmMessage args)
    {
        if (Find(ent, args.Task) is not { Status: DepartmentTaskStatus.Claimed, Kind: DepartmentTaskKind.Manual } task)
        {
            Fail(ent, args.Actor, Loc.GetString("task-board-cannot-confirm"));
            return;
        }

        if (!_economy.CanSpendDepartment(args.Actor, task.Department))
        {
            Fail(ent, args.Actor, Loc.GetString("task-board-not-head"));
            return;
        }

        if (task.ClaimedUser is not { } worker)
        {
            Fail(ent, args.Actor, Loc.GetString("task-board-no-worker"));
            return;
        }

        // Nobody signs off their own work — a Captain (head of everything) could otherwise claim and confirm at will.
        if (_players.TryGetSessionByEntity(args.Actor, out var confirmer) && confirmer.UserId == worker)
        {
            Fail(ent, args.Actor, Loc.GetString("task-board-self-confirm"));
            return;
        }

        Complete(ent, task, worker, args.Actor);
    }

    private void OnCancel(Entity<TaskBoardComponent> ent, ref TaskBoardCancelMessage args)
    {
        if (Find(ent, args.Task) is not { } task)
            return;

        if (!_economy.CanSpendDepartment(args.Actor, task.Department))
        {
            Fail(ent, args.Actor, Loc.GetString("task-board-not-head"));
            return;
        }

        ent.Comp.Tasks.Remove(task);
        _adminLog.Add(LogType.Economy, LogImpact.Low, $"{ToPrettyString(args.Actor):player} removed task '{task.Title}' from {ToPrettyString(ent)}");
        Ok(ent, args.Actor, Loc.GetString("task-board-cancelled", ("title", task.Title)));
    }

    private void OnCreate(Entity<TaskBoardComponent> ent, ref TaskBoardCreateMessage args)
    {
        var department = new ProtoId<CargoAccountPrototype>(args.Department);
        if (!ProtoMan.HasIndex<EconomyDepartmentPrototype>(department.Id) || !_economy.CanSpendDepartment(args.Actor, department))
        {
            Fail(ent, args.Actor, Loc.GetString("task-board-not-head"));
            return;
        }

        if (ent.Comp.Tasks.Count(t => t.Department == department) >= ent.Comp.MaxTasks)
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
        if (station == null || _economy.GetBalance(EconomyParty.Department(station.Value, department)) < reward)
        {
            Fail(ent, args.Actor, Loc.GetString("task-board-department-broke"));
            return;
        }

        var task = new DepartmentTask
        {
            Id = ent.Comp.NextTaskId++,
            Department = department,
            Title = title,
            Description = description,
            Reward = reward,
            Kind = DepartmentTaskKind.Manual,
            Payer = DepartmentTaskPayer.Department,
            Issuer = Name(args.Actor),
            Created = _ticker.RoundDuration(),
        };
        ent.Comp.Tasks.Add(task);
        _adminLog.Add(LogType.Economy, LogImpact.Low, $"{ToPrettyString(args.Actor):player} posted contract '{title}' ({reward}) for {department} on {ToPrettyString(ent)}");
        Ok(ent, args.Actor, Loc.GetString("task-board-created", ("title", title)));
    }

    #endregion

    #region Delivery

    private void OnInteractUsing(Entity<TaskBoardComponent> ent, ref InteractUsingEvent args)
    {
        if (args.Handled)
            return;

        // Find a delivery task that wants this item: one the user claimed first, then any open/claimed one of
        // a department the user belongs to.
        var used = args.Used;
        var user = args.User;
        _players.TryGetSessionByEntity(user, out var session);
        var candidates = ent.Comp.Tasks
            .Where(t => t.Kind == DepartmentTaskKind.Delivery && t.Status != DepartmentTaskStatus.Completed && Matches(t, used))
            .ToList();

        if (candidates.Count == 0)
            return;

        var task = candidates.FirstOrDefault(t => session != null && t.ClaimedUser == session.UserId)
                   ?? candidates.FirstOrDefault(t => _economy.IsDepartmentMember(user, t.Department));

        args.Handled = true;
        if (task == null)
        {
            _popup.PopupEntity(Loc.GetString("task-board-not-member"), ent, args.User, PopupType.SmallCaution);
            return;
        }

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

        UpdateUi(ent, null);
    }

    private bool Matches(DepartmentTask task, EntityUid item)
    {
        if (task.DeliverPrototype is { } proto && MetaData(item).EntityPrototype?.ID == proto.Id)
            return true;

        if (task.DeliverTag != null && _tag.HasTag(item, task.DeliverTag))
            return true;

        if (task.DeliverComponent is { } component && _factory.TryGetRegistration(component, out var registration) && HasComp(item, registration.Type))
        {
            if (task.DeliverContrabandSeverities.Count == 0)
                return true;

            return TryComp<ContrabandComponent>(item, out var contraband) && task.DeliverContrabandSeverities.Contains(contraband.Severity);
        }

        return false;
    }

    #endregion

    #region Progress tracking (cleaning & actions)

    /// <summary>
    /// Adds progress to every claimed task of the player (on any board) that <paramref name="matches"/>, and
    /// completes those that reached their target.
    /// </summary>
    private void Credit(EntityUid worker, int amount, Func<DepartmentTask, bool> matches)
    {
        if (amount <= 0 || !_players.TryGetSessionByEntity(worker, out var session))
            return;

        var query = EntityQueryEnumerator<TaskBoardComponent>();
        while (query.MoveNext(out var uid, out var board))
        {
            foreach (var task in board.Tasks.ToList())
            {
                if (task.Status != DepartmentTaskStatus.Claimed || task.ClaimedUser != session.UserId || !matches(task))
                    continue;

                task.DeliverProgress = Math.Min(task.DeliverTarget, task.DeliverProgress + amount);
                if (task.DeliverProgress >= task.DeliverTarget)
                    Complete((uid, board), task, session.UserId, worker);
                else
                    UpdateUi((uid, board), null);
            }
        }
    }

    private void CreditCleaning(EntityUid cleaner, int amount)
        => Credit(cleaner, amount, t => t.Kind == DepartmentTaskKind.Cleaning);

    /// <summary>Puddles already credited this round, so one puddle counts once however many strokes it takes.</summary>
    private readonly HashSet<EntityUid> _creditedPuddles = new();

    private void OnPuddleMopped(ref PuddleMoppedEvent ev)
    {
        if (!_creditedPuddles.Add(ev.Puddle))
            return;

        CreditCleaning(ev.User, 1);
    }

    private void OnDecalsCleaned(ref DecalsCleanedEvent ev)
    {
        // Tile reactions carry no user: credit the closest janitor-access mob next to the tile.
        if (!TryComp<Robust.Shared.Map.Components.MapGridComponent>(ev.Grid, out var grid))
            return;

        var coords = _map.GridTileToLocal(ev.Grid, grid, ev.Tile);
        EntityUid? best = null;
        var bestDist = float.MaxValue;
        foreach (var uid in _lookup.GetEntitiesInRange(coords, 2.5f, LookupFlags.Dynamic))
        {
            if (!HasComp<MobStateComponent>(uid) || !_players.TryGetSessionByEntity(uid, out _))
                continue;

            if (!_access.FindAccessTags(uid).Contains("Janitor"))
                continue;

            var dist = (Transform(uid).Coordinates.Position - coords.Position).Length();
            if (dist < bestDist)
            {
                bestDist = dist;
                best = uid;
            }
        }

        if (best != null)
            CreditCleaning(best.Value, ev.Count);
    }

    /// <summary>Healing items used by one player on another person count for HealDamage tasks.</summary>
    private void OnHealed(ref HealedByPlayerEvent args)
    {
        var healer = args.Healer;
        if (args.Patient == healer || !_players.TryGetSessionByEntity(healer, out _))
            return;

        // The patient is the body (healing a body part with Shitmed reports the part's body).
        var patient = TryComp<BodyPartComponent>(args.Patient, out var part) && part.Body is { } body ? body : args.Patient;
        if (!HasComp<MobStateComponent>(patient))
            return;

        var healedTotal = 0;
        var healedPerGroup = new Dictionary<string, int>();
        foreach (var (group, amount) in args.Healed.GetDamagePerGroup(ProtoMan))
        {
            if (amount <= 0)
                continue;

            var healed = (int) Math.Round(amount.Float());
            healedTotal += healed;
            healedPerGroup[group.Id] = healedPerGroup.GetValueOrDefault(group.Id) + healed;
        }

        if (healedTotal <= 0)
            return;

        Credit(healer, healedTotal, t => t is { Kind: DepartmentTaskKind.Action, Action: DepartmentTaskAction.HealDamage, ActionDamageGroup: null });
        foreach (var (group, healed) in healedPerGroup)
        {
            Credit(healer, healed, t => t is { Kind: DepartmentTaskKind.Action, Action: DepartmentTaskAction.HealDamage } && t.ActionDamageGroup == group);
        }
    }

    /// <summary>An officer changed someone's criminal status at a console: counts for SetRecordStatus tasks.</summary>
    private void OnCriminalStatusSet(ref CriminalStatusSetByOfficerEvent ev)
    {
        var oldStatus = ev.Old;
        var newStatus = ev.New;
        Credit(ev.Officer, 1, t => t is { Kind: DepartmentTaskKind.Action, Action: DepartmentTaskAction.SetRecordStatus }
                                   && t.ActionStatus == newStatus
                                   && (t.ActionFromStatus == null || t.ActionFromStatus == oldStatus));
    }

    /// <summary>Every two seconds: which beacons are the people with patrol / visit tasks standing next to?</summary>
    private void CheckPatrols()
    {
        var query = EntityQueryEnumerator<TaskBoardComponent>();
        while (query.MoveNext(out var uid, out var board))
        {
            foreach (var task in board.Tasks.ToList())
            {
                if (task.Status != DepartmentTaskStatus.Claimed || task.Kind != DepartmentTaskKind.Action
                    || task.Action is not (DepartmentTaskAction.Patrol or DepartmentTaskAction.Visit)
                    || task.ClaimedUser is not { } user)
                    continue;

                if (!_players.TryGetSessionById(user, out var session) || session.AttachedEntity is not { } mob)
                    continue;

                var changed = false;
                var coords = Transform(mob).Coordinates;
                foreach (var beaconEnt in _lookup.GetEntitiesInRange<NavMapBeaconComponent>(coords, BeaconRange))
                {
                    if (!beaconEnt.Comp.Enabled)
                        continue;

                    var name = BeaconName(beaconEnt.Comp, MetaData(beaconEnt));
                    if (name.Length == 0)
                        continue;

                    if (task.Action == DepartmentTaskAction.Visit)
                    {
                        if (name != task.ActionBeacon)
                            continue;

                        task.DeliverProgress = task.DeliverTarget;
                        changed = true;
                        break;
                    }

                    if (task.VisitedBeacons.Add(name))
                    {
                        task.DeliverProgress = Math.Min(task.DeliverTarget, task.VisitedBeacons.Count);
                        changed = true;
                        _popup.PopupEntity(Loc.GetString("task-board-patrol-beacon", ("beacon", name), ("progress", task.DeliverProgress), ("target", task.DeliverTarget)), mob, mob);
                    }
                }

                if (!changed)
                    continue;

                if (task.DeliverProgress >= task.DeliverTarget)
                    Complete((uid, board), task, user, mob);
                else
                    UpdateUi((uid, board), null);
            }
        }
    }

    #endregion

    private void Complete(Entity<TaskBoardComponent> ent, DepartmentTask task, NetUserId worker, EntityUid actor)
    {
        var station = _station.GetOwningStation(ent);
        var payee = EconomyParty.Player(worker);
        var reason = $"task '{task.Title}' ({task.Department}) on {ToPrettyString(ent)}";

        if (task.Payer == DepartmentTaskPayer.Department)
        {
            var error = EconomyPaymentError.NoAccount;
            if (station == null || !_economy.TryTransfer(EconomyParty.Department(station.Value, task.Department), payee, task.Reward, reason, out error))
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

        // Keep a short history per department, drop the oldest completed ones.
        var completed = ent.Comp.Tasks.Where(t => t.Status == DepartmentTaskStatus.Completed && t.Department == task.Department).ToList();
        while (completed.Count > 4)
        {
            ent.Comp.Tasks.Remove(completed[0]);
            completed.RemoveAt(0);
        }

        ent.Comp.Message = Loc.GetString("task-board-completed", ("title", task.Title), ("reward", task.Reward));
        UpdateUi(ent, null);
    }
}
