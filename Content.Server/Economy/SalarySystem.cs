// iss14: Economy
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Afk;
using Content.Server.Chat.Managers;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Shared.Bed.Cryostorage;
using Content.Shared.CCVar;
using Content.Shared.Economy;
using Content.Shared.GameTicking;
using Content.Shared.Ghost.Components;
using Content.Shared.Mind;
using Content.Shared.Mobs.Systems;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.Economy;

/// <summary>
/// Pays every crew member once per round, at round end — including players who have disconnected by then.
///
/// Time is tracked here (not via the playtime trackers): every <see cref="TickInterval"/> each in-game session
/// whose mind holds a job accrues that interval for that job while they are in their body (alive or dead) or
/// a ghost whose body died; observers, cryo-stored characters and AFK players accrue nothing.
///
/// Payout = Σ(base(job) × time(job)) / presentTime × factor(presentTime / roundDuration) × multiplier, i.e. the
/// time-weighted average base of all jobs held this round (switching jobs can't be exploited) scaled by
/// presence: <c>economy.salary_min_factor</c> when barely present, 1.0 at <c>economy.salary_full_time_fraction</c>
/// of the round, <c>economy.salary_max_factor</c> for the whole round. Base salaries come from the DB override
/// table, else <see cref="SalaryTablePrototype"/>, else <c>economy.salary_default</c>.
/// </summary>
public sealed partial class SalarySystem : EntitySystem
{
    [Dependency] private EconomySystem _economy = default!;
    [Dependency] private IBankManager _bank = default!;
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IAfkManager _afk = default!;
    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ServerGameTicker _ticker = default!;
    [Dependency] private SharedMindSystem _mind = default!;
    [Dependency] private SharedJobSystem _jobs = default!;
    [Dependency] private MobStateSystem _mobState = default!;

    public const string DefaultTable = "Default";
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);

    private TimeSpan _nextTick;

    /// <summary>Per player, per job: time accrued this round. Kept after disconnect until the round ends.</summary>
    private readonly Dictionary<NetUserId, RoundPay> _round = new();

    /// <summary>Admin overrides of per-round base salaries (DB-backed).</summary>
    private readonly Dictionary<string, int> _overrides = new();

    private bool _paidThisRound;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RoundEndTextAppendEvent>(OnRoundEndText);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);

        _ = LoadOverridesAsync();
    }

    private async Task LoadOverridesAsync()
    {
        try
        {
            var rows = await _db.GetSalaryOverridesAsync();
            _overrides.Clear();
            foreach (var (job, amount) in rows)
            {
                _overrides[job] = amount;
            }
        }
        catch (Exception e)
        {
            Log.Error($"Failed to load salary overrides: {e}");
        }
    }

    #region Time tracking

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.CurTime < _nextTick)
            return;

        _nextTick = _timing.CurTime + TickInterval;

        if (_ticker.RunLevel != GameRunLevel.InRound || !_cfg.GetCVar(CCVars.EconomySalaryEnabled))
            return;

        foreach (var session in _players.Sessions)
        {
            if (session.Status != SessionStatus.InGame)
                continue;

            if (TryGetPaidJob(session, out var job) && job is { } paidJob)
                Accrue(session.UserId, paidJob, TickInterval);
        }
    }

    /// <summary>The job a session is currently earning for, if any (see class docs for the rules).</summary>
    public bool TryGetPaidJob(ICommonSession session, out ProtoId<JobPrototype>? job)
    {
        job = null;

        if (_afk.IsAfk(session))
            return false;

        if (!_mind.TryGetMind(session.UserId, out var mindId, out var mind))
            return false;

        if (!_jobs.MindTryGetJobId(mindId, out job) || job == null)
            return false;

        var current = mind.CurrentEntity;
        if (current == null)
            return false;

        if (HasComp<GhostComponent>(current))
        {
            // Dead and ghosted still counts ("killed on duty"); a living abandoned body or cryo doesn't.
            if (!TryGetEntity(mind.OriginalOwnedEntity, out var body))
                return false;

            return _mobState.IsDead(body.Value) && !HasComp<CryostorageContainedComponent>(body);
        }

        return !HasComp<CryostorageContainedComponent>(current);
    }

    /// <summary>Adds job time for a player (also used by tests).</summary>
    public void Accrue(NetUserId userId, ProtoId<JobPrototype> job, TimeSpan time)
    {
        if (time <= TimeSpan.Zero)
            return;

        if (!_round.TryGetValue(userId, out var pay))
            _round[userId] = pay = new RoundPay();

        pay.JobTime[job] = pay.JobTime.GetValueOrDefault(job) + time;
    }

    /// <summary>Accrued job time for a player this round (tests, admin commands).</summary>
    public IReadOnlyDictionary<ProtoId<JobPrototype>, TimeSpan> GetAccrued(NetUserId userId)
        => _round.TryGetValue(userId, out var pay) ? pay.JobTime : new Dictionary<ProtoId<JobPrototype>, TimeSpan>();

    #endregion

    #region Base salaries

    /// <summary>Per-round base salary for a job: admin override, salary table, default CVar.</summary>
    public int GetBaseSalary(ProtoId<JobPrototype> job)
    {
        if (_overrides.TryGetValue(job.Id, out var overridden))
            return overridden;

        return GetPrototypeSalary(job);
    }

    /// <summary>The salary table's value for a job (ignoring overrides), or the default CVar.</summary>
    public int GetPrototypeSalary(ProtoId<JobPrototype> job)
    {
        if (ProtoMan.TryIndex<SalaryTablePrototype>(DefaultTable, out var table) && table.Jobs.TryGetValue(job, out var rate))
            return rate;

        return _cfg.GetCVar(CCVars.EconomySalaryDefault);
    }

    public bool HasOverride(ProtoId<JobPrototype> job) => _overrides.ContainsKey(job.Id);

    /// <summary>Sets (or with null, clears) the admin override for a job and persists it.</summary>
    public void SetOverride(ProtoId<JobPrototype> job, int? amount)
    {
        if (amount == null)
            _overrides.Remove(job.Id);
        else
            _overrides[job.Id] = Math.Max(0, amount.Value);

        var value = amount == null ? (int?) null : Math.Max(0, amount.Value);
        _bank.EnqueueWrite(() => _db.SetSalaryOverrideAsync(job.Id, value));
    }

    #endregion

    #region Payout math

    /// <summary>
    /// Presence factor: <paramref name="minFactor"/> at 0, 1.0 at <paramref name="fullFraction"/>,
    /// <paramref name="maxFactor"/> at 1.0 (linear in between, clamped).
    /// </summary>
    public static float PresenceFactor(double presentFraction, float minFactor, float fullFraction, float maxFactor)
    {
        presentFraction = Math.Clamp(presentFraction, 0, 1);
        fullFraction = Math.Clamp(fullFraction, 0.01f, 1f);
        minFactor = Math.Max(0, minFactor);
        maxFactor = Math.Max(1f, maxFactor);

        if (presentFraction >= fullFraction)
        {
            if (fullFraction >= 1f)
                return maxFactor;

            var t = (presentFraction - fullFraction) / (1 - fullFraction);
            return (float) (1 + (maxFactor - 1) * t);
        }

        return (float) (minFactor + (1 - minFactor) * (presentFraction / fullFraction));
    }

    /// <summary>
    /// Gross payout for the given job times: time-weighted average base × presence factor × multiplier.
    /// </summary>
    public long ComputePayout(IReadOnlyDictionary<ProtoId<JobPrototype>, TimeSpan> jobTime, TimeSpan roundDuration, out float factor, out double weightedBase)
    {
        factor = 0;
        weightedBase = 0;

        var present = TimeSpan.Zero;
        foreach (var time in jobTime.Values)
        {
            present += time;
        }

        if (present <= TimeSpan.Zero)
            return 0;

        foreach (var (job, time) in jobTime)
        {
            weightedBase += GetBaseSalary(job) * (time / present);
        }

        var fraction = roundDuration > TimeSpan.Zero ? present / roundDuration : 1.0;
        factor = PresenceFactor(fraction,
            _cfg.GetCVar(CCVars.EconomySalaryMinFactor),
            _cfg.GetCVar(CCVars.EconomySalaryFullTimeFraction),
            _cfg.GetCVar(CCVars.EconomySalaryMaxFactor));

        var multiplier = Math.Max(0, _cfg.GetCVar(CCVars.EconomySalaryMultiplier));
        return (long) Math.Floor(weightedBase * factor * multiplier);
    }

    #endregion

    #region Round end

    private void OnRoundEndText(ref RoundEndTextAppendEvent ev)
    {
        var (count, total) = PayAll();
        if (count > 0)
            ev.AddLine(Loc.GetString("salary-round-end-summary", ("count", count), ("total", total)));
    }

    /// <summary>Pays everyone who accrued time this round. Idempotent per round.</summary>
    public (int Count, long Total) PayAll()
    {
        if (_paidThisRound || !_cfg.GetCVar(CCVars.EconomySalaryEnabled))
            return (0, 0);

        _paidThisRound = true;

        var roundDuration = _ticker.RoundDuration();
        var count = 0;
        var total = 0L;

        foreach (var (userId, pay) in _round)
        {
            var gross = ComputePayout(pay.JobTime, roundDuration, out var factor, out _);
            if (gross <= 0)
                continue;

            // Let other systems (fines) take their cut.
            var deduction = new SalaryDeductionEvent(userId, gross);
            RaiseLocalEvent(ref deduction);
            var net = Math.Max(0, gross - deduction.Total);

            var jobs = string.Join(", ", pay.JobTime.Select(kv => $"{kv.Key.Id} {kv.Value.TotalMinutes:0}min"));
            if (net > 0)
            {
                _economy.Pay(EconomyParty.Player(userId), net, $"salary ({jobs}; factor {factor:0.00})");
                count++;
                total += net;
            }

            if (_players.TryGetSessionById(userId, out var session) && session.Status != SessionStatus.Disconnected)
            {
                var jobNames = string.Join(", ", pay.JobTime.Keys.Select(j => ProtoMan.TryIndex(j, out var proto) ? proto.LocalizedName : j.Id));
                _chat.DispatchServerMessage(session, Loc.GetString("salary-notice",
                    ("credits", net),
                    ("jobs", jobNames),
                    ("factor", factor.ToString("0.00"))));

                foreach (var line in deduction.Lines)
                {
                    _chat.DispatchServerMessage(session, line);
                }
            }
        }

        return (count, total);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        ResetRound();
    }

    /// <summary>Forgets all accrued time and the paid flag (round restart, tests).</summary>
    public void ResetRound()
    {
        _round.Clear();
        _paidThisRound = false;
    }

    #endregion

    private sealed class RoundPay
    {
        public readonly Dictionary<ProtoId<JobPrototype>, TimeSpan> JobTime = new();
    }
}

/// <summary>
/// Raised before a salary is paid so other systems can deduct from it (unpaid fines). Handlers add to
/// <see cref="Total"/> (never more than <see cref="Gross"/> in sum) and may add a chat line for the player.
/// </summary>
[ByRefEvent]
public record struct SalaryDeductionEvent(NetUserId UserId, long Gross)
{
    public long Total;
    public readonly List<string> Lines = new();
}
