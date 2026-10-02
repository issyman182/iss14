// iss14: Economy
using System.Linq;
using Content.Server.Chat.Managers;
using Content.Server.GameTicking.Events;
using Content.Server.Players.PlayTimeTracking;
using Content.Shared.CCVar;
using Content.Shared.Economy;
using Content.Shared.GameTicking;
using Content.Shared.Players.PlayTimeTracking;
using Content.Shared.Roles;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server.Economy;

/// <summary>
/// Pays players for the time they spent in a job this round. Playtime comes from the existing job trackers
/// (<see cref="PlayTimeTrackingManager"/>; they only accrue while the player is alive and not AFK). A baseline of
/// every tracker is taken when the session enters the game; the payout is the growth since that baseline minus
/// what was already paid, so a reconnect or the round end can never pay the same minutes twice.
/// </summary>
public sealed partial class SalarySystem : EntitySystem
{
    [Dependency] private IBankManager _bank = default!;
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private PlayTimeTrackingManager _tracking = default!;

    public const string DefaultTable = "Default";

    private readonly Dictionary<NetUserId, SessionPay> _sessions = new();

    public override void Initialize()
    {
        base.Initialize();

        _players.PlayerStatusChanged += OnPlayerStatusChanged;

        SubscribeLocalEvent<PlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<PlayerDisconnectingEvent>(OnPlayerDisconnecting);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _players.PlayerStatusChanged -= OnPlayerStatusChanged;
        _sessions.Clear();
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs e)
    {
        // Disconnects are settled (and the entry removed) in OnPlayerDisconnecting, which the game ticker raises
        // before the session's playtime/bank data is unloaded, regardless of event handler ordering.
        if (e.NewStatus == SessionStatus.InGame)
            EnsureBaseline(e.Session);
    }

    private void OnPlayerAttached(PlayerAttachedEvent ev)
    {
        EnsureBaseline(ev.Player);
    }

    private void OnPlayerDisconnecting(PlayerDisconnectingEvent ev)
    {
        PayOut(ev.Session);
        _sessions.Remove(ev.Session.UserId);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        foreach (var session in _players.Sessions)
        {
            PayOut(session);
            // Fresh round: whatever accrued so far is settled, start counting from here again.
            ResetBaseline(session);
        }
    }

    /// <summary>Takes the tracker snapshot for a session once, as soon as its playtime data is loaded.</summary>
    private void EnsureBaseline(ICommonSession session)
    {
        if (_sessions.ContainsKey(session.UserId))
            return;

        if (!_tracking.TryGetTrackerTimes(session, out var times))
            return;

        _tracking.FlushTracker(session);
        _sessions[session.UserId] = new SessionPay(new Dictionary<string, TimeSpan>(times));
    }

    private void ResetBaseline(ICommonSession session)
    {
        _sessions.Remove(session.UserId);
        EnsureBaseline(session);
    }

    /// <summary>Hourly rate for a job: the salary table, else the default CVar.</summary>
    public int GetHourlyRate(ProtoId<JobPrototype> job)
    {
        if (ProtoMan.TryIndex<SalaryTablePrototype>(DefaultTable, out var table)
            && table.Jobs.TryGetValue(job, out var rate))
        {
            return rate;
        }

        return _cfg.GetCVar(CCVars.EconomySalaryDefaultPerHour);
    }

    /// <summary>
    /// Maps every job playtime tracker to the best-paid job using it and that job's localized name.
    /// Only job trackers count — never <c>Overall</c>/<c>Admin</c>.
    /// </summary>
    private Dictionary<string, (ProtoId<JobPrototype> Job, int Rate, string Name)> BuildTrackerRates()
    {
        var result = new Dictionary<string, (ProtoId<JobPrototype>, int, string)>();
        foreach (var job in ProtoMan.EnumeratePrototypes<JobPrototype>())
        {
            if (string.IsNullOrEmpty(job.PlayTimeTracker.Id))
                continue;

            var rate = GetHourlyRate(job.ID);
            if (result.TryGetValue(job.PlayTimeTracker.Id, out var existing) && existing.Item2 >= rate)
                continue;

            result[job.PlayTimeTracker.Id] = (job.ID, rate, job.LocalizedName);
        }

        return result;
    }

    /// <summary>
    /// Pure payout computation: for each job tracker, the unpaid minutes since the baseline and the credits owed.
    /// </summary>
    public List<SalaryLine> ComputeLines(
        IReadOnlyDictionary<string, TimeSpan> baseline,
        IReadOnlyDictionary<string, TimeSpan> current,
        IReadOnlyDictionary<string, TimeSpan> alreadyPaid)
    {
        var lines = new List<SalaryLine>();
        var rates = BuildTrackerRates();
        var multiplier = _cfg.GetCVar(CCVars.EconomySalaryMultiplier);

        foreach (var (tracker, now) in current)
        {
            if (tracker == PlayTimeTrackingShared.TrackerOverall || tracker == PlayTimeTrackingShared.TrackerAdmin)
                continue;

            if (!rates.TryGetValue(tracker, out var rate))
                continue;

            var earned = now - baseline.GetValueOrDefault(tracker) - alreadyPaid.GetValueOrDefault(tracker);
            if (earned <= TimeSpan.Zero)
                continue;

            var credits = EconomyPricing.Salary(earned.TotalMinutes, rate.Rate, multiplier);
            lines.Add(new SalaryLine(tracker, rate.Job, rate.Name, earned, credits));
        }

        return lines;
    }

    /// <summary>Pays everything the session has earned since its last payout. Safe to call repeatedly.</summary>
    public long PayOut(ICommonSession session)
    {
        if (!_cfg.GetCVar(CCVars.EconomySalaryEnabled))
            return 0;

        // A session without a baseline (playtime loaded late) starts counting now; nothing is owed yet.
        EnsureBaseline(session);

        if (!_sessions.TryGetValue(session.UserId, out var pay))
            return 0;

        if (!_tracking.TryGetTrackerTimes(session, out var current))
            return 0;

        _tracking.FlushTracker(session);

        var lines = ComputeLines(pay.Baseline, current, pay.Paid);
        var total = 0L;
        var notices = new List<string>();

        foreach (var line in lines)
        {
            // Always mark the minutes as settled, even when they rounded down to 0 credits; the fraction is lost
            // rather than carried, which keeps reconnect/round-end bookkeeping trivial.
            pay.Paid[line.Tracker] = pay.Paid.GetValueOrDefault(line.Tracker) + line.Time;

            if (line.Credits <= 0)
                continue;

            total += line.Credits;
            notices.Add(Loc.GetString("salary-notice",
                ("credits", line.Credits),
                ("minutes", (int) Math.Floor(line.Time.TotalMinutes)),
                ("job", line.JobName)));
        }

        if (total <= 0)
            return 0;

        var reason = $"salary ({string.Join(", ", lines.Where(l => l.Credits > 0).Select(l => $"{l.Job} {l.Time.TotalMinutes:0}min"))})";

        if (_bank.IsLoaded(session.UserId))
        {
            _bank.Credit(session.UserId, total, reason, out _, salary: true);
        }
        else
        {
            // Cache already unloaded (late disconnect path): write straight to the database.
            _ = _bank.AdjustBalanceAsync(session.UserId, total, reason);
        }

        if (session.Status != SessionStatus.Disconnected)
        {
            foreach (var notice in notices)
            {
                _chat.DispatchServerMessage(session, notice);
            }
        }

        return total;
    }

    /// <summary>Testing hook: whether a baseline exists for the session.</summary>
    public bool HasBaseline(ICommonSession session) => _sessions.ContainsKey(session.UserId);

    private sealed class SessionPay(Dictionary<string, TimeSpan> baseline)
    {
        public readonly Dictionary<string, TimeSpan> Baseline = baseline;
        public readonly Dictionary<string, TimeSpan> Paid = new();
    }
}

/// <summary>One job's share of a salary payout.</summary>
public readonly record struct SalaryLine(string Tracker, ProtoId<JobPrototype> Job, string JobName, TimeSpan Time, long Credits);
