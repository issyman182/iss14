using System.Collections.Generic;
using Content.Server.Administration;
using Content.Server.Antag;
using Content.Server.Antag.Components;
using Content.Server.Chat.Managers;
using Content.Server.GameTicking;
using Content.Server.RoundEnd;
using Content.Shared.Antag;
using Content.Shared.Chat;
using Content.Shared.GameTicking;
using Content.Shared.Ghost;
using Content.Shared.Mind;
using Content.Shared.Mobs;
using Robust.Shared.Prototypes;
using Robust.Server.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Content.Shared.Ghost.Systems;
using Content.Shared.Ghost.Components;

namespace Content.Server.NewLife;

/// <summary>
/// Runtime logic for the "New Life" rule. Tracks time of death per player, computes respawn eligibility
/// (cooldown elapsed, per-round cap not reached, not blocked by an active event/evac), syncs that state to
/// the player's ghost for the UI, and handles the actual new-life request by routing the player back through
/// character selection via <see cref="GameTicker.Respawn"/> (which leaves the old corpse soulless).
/// </summary>
public sealed partial class NewLifeSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private GameTicker _gameTicker = default!;
    [Dependency] private RoundEndSystem _roundEnd = default!;
    [Dependency] private SharedGhostSystem _ghost = default!;
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private SharedMindSystem _mind = default!;
    [Dependency] private AntagSelectionSystem _antag = default!;
    [Dependency] private NewLifeManager _newLife = default!;

    /// <summary>When each player most recently died, used to drive the respawn cooldown.</summary>
    private readonly Dictionary<NetUserId, TimeSpan> _deathTimes = new();

    /// <summary>
    /// Antag roles captured when a player takes a new life (rule entity + antag specifier), re-granted to their
    /// new character on spawn when <c>new_life_antag_keep_antag</c> is set.
    /// </summary>
    private readonly Dictionary<NetUserId, List<(EntityUid Rule, ProtoId<AntagSpecifierPrototype> Proto)>> _pendingKeepAntag = new();

    private TimeSpan _nextSync;
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(1);

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnSpawnComplete);
        SubscribeNetworkEvent<GhostNewLifeRequest>(OnNewLifeRequest);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        _newLife.ResetRound();
        _deathTimes.Clear();
        _pendingKeepAntag.Clear();
    }

    /// <summary>
    /// Records which active antag rule(s) and specifier(s) the player currently belongs to, so we can re-grant
    /// them to the new character once it spawns.
    /// </summary>
    private void CaptureAntagRoles(NetUserId user)
    {
        if (!_mind.TryGetMind(user, out var mindId, out _))
            return;

        var captured = new List<(EntityUid, ProtoId<AntagSpecifierPrototype>)>();
        var query = _gameTicker.GetActiveGameRules<AntagSelectionComponent>();
        foreach (var (ruleUid, comp) in query)
        {
            foreach (var (proto, minds) in comp.AssignedMinds)
            {
                foreach (var (assignedMind, _) in minds)
                {
                    if (assignedMind != mindId.Value)
                        continue;

                    captured.Add((ruleUid, proto));
                    break;
                }
            }
        }

        if (captured.Count != 0)
            _pendingKeepAntag[user] = captured;
    }

    private void OnSpawnComplete(PlayerSpawnCompleteEvent ev)
    {
        if (!_pendingKeepAntag.Remove(ev.Player.UserId, out var roles))
            return;

        foreach (var (rule, proto) in roles)
        {
            // Rule may have ended between dying and respawning; skip if so.
            if (!TryComp<AntagSelectionComponent>(rule, out var comp))
                continue;

            _antag.TryMakeAntag((rule, comp), proto, ev.Player, checkPref: false);
        }
    }

    private void OnMobStateChanged(MobStateChangedEvent args)
    {
        if (args.NewMobState != MobState.Dead)
            return;

        if (!TryComp<ActorComponent>(args.Target, out var actor))
            return;

        // Reset the cooldown each time the player dies (including after a previous new life).
        _deathTimes[actor.PlayerSession.UserId] = _timing.CurTime;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.CurTime < _nextSync)
            return;
        _nextSync = _timing.CurTime + SyncInterval;

        var blocked = IsBlockedNow(out _);
        var enabled = _newLife.Enabled;
        var cooldown = TimeSpan.FromSeconds(_newLife.Cooldown);
        var max = _newLife.MaxLives;

        var query = EntityQueryEnumerator<GhostComponent, ActorComponent>();
        while (query.MoveNext(out var uid, out var ghost, out var actor))
        {
            var user = actor.PlayerSession.UserId;
            // Only players who actually died this life are eligible — keeps observers / admin-ghosts out.
            var hasDeath = _deathTimes.TryGetValue(user, out var death);
            var eligibleTime = hasDeath ? death + cooldown : _timing.CurTime;
            // 0 = none/feature off/never died (button hidden); -1 = unlimited.
            var remaining = !enabled || !hasDeath
                ? 0
                : (max == 0 ? -1 : Math.Max(0, max - _newLife.GetUsed(user)));

            _ghost.SetNewLifeState((uid, ghost), eligibleTime, remaining, enabled && !blocked);
        }
    }

    /// <summary>
    /// Whether new lives are blocked right now (wrong run level, evac called, or an active blocklisted rule).
    /// </summary>
    public bool IsBlockedNow(out string reason)
    {
        if (_gameTicker.RunLevel != GameRunLevel.InRound)
        {
            reason = Loc.GetString("new-life-blocked-not-in-round");
            return true;
        }

        if (_roundEnd.IsRoundEndRequested())
        {
            reason = Loc.GetString("new-life-blocked-evac");
            return true;
        }

        var blockedSet = _newLife.GetBlockedSet();
        if (blockedSet.Count != 0)
        {
            foreach (var rule in _gameTicker.GetActiveGameRules())
            {
                var id = MetaData(rule).EntityPrototype?.ID;
                if (id != null && blockedSet.Contains(id))
                {
                    reason = Loc.GetString("new-life-blocked-event", ("event", id));
                    return true;
                }
            }
        }

        reason = string.Empty;
        return false;
    }

    private void OnNewLifeRequest(GhostNewLifeRequest msg, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;

        if (!_newLife.Enabled)
            return;

        // Must be controlling their own ghost.
        if (session.AttachedEntity is not { Valid: true } attached
            || !TryComp<GhostComponent>(attached, out _)
            || !TryComp<ActorComponent>(attached, out var actor)
            || actor.PlayerSession != session)
        {
            Log.Warning($"User {session.Name} sent an invalid {nameof(GhostNewLifeRequest)}");
            return;
        }

        if (IsBlockedNow(out _))
            return;

        var user = session.UserId;

        // Must have actually died this life (excludes observers / admin-ghosts).
        if (!_deathTimes.TryGetValue(user, out var death))
            return;

        // Cooldown check.
        if (_timing.CurTime < death + TimeSpan.FromSeconds(_newLife.Cooldown))
            return;

        // Per-round cap (0 = unlimited).
        var max = _newLife.MaxLives;
        if (max != 0 && _newLife.GetUsed(user) >= max)
            return;

        // If configured, remember this player's antag roles so they can be re-granted on the new character.
        if (_newLife.KeepAntag)
            CaptureAntagRoles(user);

        _newLife.IncrementUsed(user);
        // Clear the cooldown marker so a fresh death is required before the next new life.
        _deathTimes.Remove(user);

        var reborn = Loc.GetString("new-life-reborn-message");
        _chat.DispatchServerMessage(session, reborn);

        // Wipes the mind off the old corpse (left soulless) and drops the player into the lobby to pick a new
        // character before late-joining.
        _gameTicker.Respawn(session);
    }
}
