using System.Linq;
using System.Text.Json;
using Content.Server.GameTicking;
using Content.Shared.Administration;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.GameTicking;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Shared.ContentPack;
using Robust.Shared.Player;
using Robust.Shared.Network;
using Robust.Shared.Utility;

namespace Content.Server.Administration.RoundStats;

/// <summary>
/// Tracks per-player combat statistics (kills, damage dealt, deaths) for the current round, in memory.
/// Resets on round restart. Surfaced read-only by the <c>combatstats</c> admin EUI.
/// </summary>
/// <remarks>
/// Standalone (does not use <c>KillTrackerComponent</c>/<c>KillReportedEvent</c>, which only exist for the
/// DeathMatch game mode). Damage is attributed via <see cref="DamageChangedEvent.Origin"/>; kills are
/// attributed on death to the finishing-blow origin, falling back to the largest cumulative damage source.
/// </remarks>
public sealed partial class RoundStatsSystem : EntitySystem
{
    [Dependency] private IResourceManager _res = default!;

    private static readonly ResPath Dir = new("/combat_stats");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly Dictionary<NetUserId, PlayerCombatStats> _stats = new();

    /// <summary>Damage dealt to each victim by each player during that victim's current life (kill attribution).</summary>
    private readonly Dictionary<EntityUid, Dictionary<NetUserId, FixedPoint2>> _damageToVictim = new();

    /// <summary>The round id the current in-memory stats belong to (captured while combat happens).</summary>
    private int _roundId;

    private GameTicker? _ticker;
    private GameTicker Ticker => _ticker ??= EntityManager.System<GameTicker>();

    /// <summary>The id of the round currently in progress.</summary>
    public int CurrentRoundId => Ticker.RoundId;

    private static ResPath FilePath(int round) => new($"/combat_stats/round_{round}.json");

    public override void Initialize()
    {
        SubscribeLocalEvent<DamageableComponent, DamageChangedEvent>(OnDamageChanged);
        // Broadcast form: the directed (MobStateComponent, MobStateChangedEvent) pair is already
        // claimed by the mob-state systems, and Robust allows only one directed sub per (comp, event).
        SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        // iss14 fix: drop per-victim attribution when the victim entity goes away.
        SubscribeLocalEvent<MobStateComponent, EntityTerminatingEvent>(OnVictimTerminating);
    }

    private void OnVictimTerminating(Entity<MobStateComponent> ent, ref EntityTerminatingEvent args)
    {
        _damageToVictim.Remove(ent.Owner);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        // Persist the just-ended round's stats to disk before clearing, so it can be browsed later.
        SaveCurrentRound();

        _stats.Clear();
        _damageToVictim.Clear();
        _roundId = 0;
    }

    private PlayerCombatStats Get(NetUserId id)
    {
        // Remember which round these in-memory stats belong to (for persistence at round end).
        _roundId = CurrentRoundId;

        if (!_stats.TryGetValue(id, out var s))
            _stats[id] = s = new PlayerCombatStats();
        return s;
    }

    private void OnDamageChanged(EntityUid uid, DamageableComponent component, DamageChangedEvent args)
    {
        if (!args.DamageIncreased || args.DamageDelta == null)
            return;

        // Only attribute damage that originated from a player.
        if (!TryComp<ActorComponent>(args.Origin, out var actor))
            return;

        // Ignore self-inflicted damage.
        if (args.Origin == uid)
            return;

        var amount = args.DamageDelta.GetTotal();
        if (amount <= 0)
            return;

        var attackerId = actor.PlayerSession.UserId;
        var stats = Get(attackerId);
        stats.LastOocName = actor.PlayerSession.Name;
        stats.LastCharacterName = MetaData(args.Origin!.Value).EntityName;
        stats.DamageTotal += amount;

        if (HasComp<ActorComponent>(uid))
            stats.DamagePlayers += amount;

        // Record cumulative damage to this victim for later kill attribution.
        // iss14 fix: only mobs can die, so only track mobs; otherwise every damaged wall/window/item
        // accumulated an entry for the whole round.
        if (!HasComp<MobStateComponent>(uid))
            return;

        if (!_damageToVictim.TryGetValue(uid, out var sources))
            _damageToVictim[uid] = sources = new Dictionary<NetUserId, FixedPoint2>();
        sources[attackerId] = sources.GetValueOrDefault(attackerId) + amount;
    }

    private void OnMobStateChanged(MobStateChangedEvent args)
    {
        // Count a "kill" only on the transition into Dead.
        if (args.NewMobState != MobState.Dead || args.OldMobState == MobState.Dead)
            return;

        var victim = args.Target;
        var victimIsPlayer = TryComp<ActorComponent>(victim, out var victimActor);
        var victimId = victimActor?.PlayerSession.UserId;

        // Killer = finishing-blow origin if a player, otherwise the largest cumulative damage source.
        NetUserId? killerId = null;
        if (TryComp<ActorComponent>(args.Origin, out var originActor))
            killerId = originActor.PlayerSession.UserId;
        else if (_damageToVictim.TryGetValue(victim, out var sources))
            killerId = GetLargestSource(sources);

        // Don't count a suicide as a kill.
        if (killerId != null && !(victimIsPlayer && killerId == victimId))
        {
            var killer = Get(killerId.Value);
            killer.KillsTotal++;
            if (victimIsPlayer)
                killer.KillsPlayers++;
        }

        if (victimIsPlayer && victimActor != null)
        {
            var victimStats = Get(victimActor.PlayerSession.UserId);
            victimStats.LastOocName = victimActor.PlayerSession.Name;
            victimStats.LastCharacterName = MetaData(victim).EntityName;
            victimStats.Deaths++;
        }

        _damageToVictim.Remove(victim);
    }

    private static NetUserId? GetLargestSource(Dictionary<NetUserId, FixedPoint2> sources)
    {
        NetUserId? max = null;
        var maxDamage = FixedPoint2.Zero;
        foreach (var (id, dmg) in sources)
        {
            if (dmg < maxDamage)
                continue;
            max = id;
            maxDamage = dmg;
        }

        return max;
    }

    /// <summary>Builds a transport-safe snapshot of the current stats (FixedPoint2 → float).</summary>
    public List<CombatStatEntry> BuildEntries()
    {
        var result = new List<CombatStatEntry>(_stats.Count);
        foreach (var (_, s) in _stats)
        {
            var character = string.IsNullOrEmpty(s.LastCharacterName) ? s.LastOocName : s.LastCharacterName;
            result.Add(new CombatStatEntry(
                s.LastOocName,
                character,
                s.KillsPlayers,
                s.KillsTotal,
                (float) s.DamagePlayers,
                (float) s.DamageTotal,
                s.Deaths));
        }

        return result;
    }

    /// <summary>Rounds that have combat stats available: every saved round plus the current one, newest first.</summary>
    public List<int> GetAvailableRounds()
    {
        var rounds = new HashSet<int>();

        if (_res.UserData.Exists(Dir))
        {
            foreach (var entry in _res.UserData.DirectoryEntries(Dir))
            {
                if (TryParseRound(entry, out var id))
                    rounds.Add(id);
            }
        }

        // Always allow viewing the live current round.
        var current = CurrentRoundId;
        if (current > 0)
            rounds.Add(current);

        return rounds.OrderByDescending(r => r).ToList();
    }

    /// <summary>Stats for a round: the live in-memory set for the current round, or the saved snapshot for a past one.</summary>
    public List<CombatStatEntry> GetRoundStats(int roundId)
    {
        if (roundId == CurrentRoundId)
            return BuildEntries();

        var path = FilePath(roundId);
        if (!_res.UserData.Exists(path))
            return new List<CombatStatEntry>();

        try
        {
            using var reader = _res.UserData.OpenText(path);
            var stored = JsonSerializer.Deserialize<List<StoredStat>>(reader.ReadToEnd(), JsonOpts);
            if (stored == null)
                return new List<CombatStatEntry>();

            return stored
                .Select(s => new CombatStatEntry(
                    s.OocName,
                    s.CharacterName,
                    s.KillsPlayers,
                    s.KillsTotal,
                    s.DamagePlayers,
                    s.DamageTotal,
                    s.Deaths))
                .ToList();
        }
        catch (Exception e)
        {
            Log.Error($"Failed to read combat stats for round {roundId}: {e}");
            return new List<CombatStatEntry>();
        }
    }

    private void SaveCurrentRound()
    {
        if (_stats.Count == 0 || _roundId <= 0)
            return;

        var stored = BuildEntries()
            .Select(e => new StoredStat
            {
                OocName = e.OocName,
                CharacterName = e.CharacterName,
                KillsPlayers = e.KillsPlayers,
                KillsTotal = e.KillsTotal,
                DamagePlayers = e.DamagePlayers,
                DamageTotal = e.DamageTotal,
                Deaths = e.Deaths,
            })
            .ToList();

        try
        {
            _res.UserData.CreateDir(Dir);
            using var writer = _res.UserData.OpenWriteText(FilePath(_roundId));
            writer.Write(JsonSerializer.Serialize(stored, JsonOpts));
        }
        catch (Exception e)
        {
            Log.Error($"Failed to save combat stats for round {_roundId}: {e}");
        }
    }

    private static bool TryParseRound(string fileName, out int round)
    {
        round = 0;
        // Expecting "round_<id>.json".
        if (!fileName.StartsWith("round_", StringComparison.Ordinal) ||
            !fileName.EndsWith(".json", StringComparison.Ordinal))
            return false;

        var idPart = fileName["round_".Length..^".json".Length];
        return int.TryParse(idPart, out round);
    }

    private sealed class StoredStat
    {
        public string OocName { get; set; } = string.Empty;
        public string CharacterName { get; set; } = string.Empty;
        public int KillsPlayers { get; set; }
        public int KillsTotal { get; set; }
        public float DamagePlayers { get; set; }
        public float DamageTotal { get; set; }
        public int Deaths { get; set; }
    }

    private sealed class PlayerCombatStats
    {
        public string LastOocName = string.Empty;
        public string LastCharacterName = string.Empty;
        public int KillsPlayers;
        public int KillsTotal;
        public FixedPoint2 DamagePlayers;
        public FixedPoint2 DamageTotal;
        public int Deaths;
    }
}
