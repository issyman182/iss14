using System.Linq;
using Content.Server.GameTicking.Events;
using Content.Shared.Administration;
using Content.Shared.GameTicking;
using Robust.Shared.Timing;

namespace Content.Server.Administration.Performance;

/// <summary>
/// Counts live entities per prototype for the <c>entitycensus</c> admin panel.
/// </summary>
/// <remarks>
/// A full census iterates every entity, so it only happens on demand (while a panel is open,
/// at most once per <see cref="CacheAge"/>) plus once at round start for the round baseline.
/// </remarks>
public sealed partial class EntityCensusSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;

    /// <summary>How many prototypes are sent to the panel.</summary>
    public const int MaxEntries = 100;

    /// <summary>A cached census younger than this is reused instead of recounting.</summary>
    private static readonly TimeSpan CacheAge = TimeSpan.FromSeconds(4);

    /// <summary>Roughly how far back the minute delta looks.</summary>
    private static readonly TimeSpan MinuteWindow = TimeSpan.FromSeconds(60);

    private Dictionary<string, int> _baseline = new();

    private Dictionary<string, int> _cached = new();
    private int _cachedTotal;
    private TimeSpan _cachedAt = TimeSpan.MinValue;

    /// <summary>Past censuses used for the minute delta, oldest first.</summary>
    private readonly Queue<(TimeSpan Time, Dictionary<string, int> Counts)> _history = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RoundStartingEvent>(OnRoundStarting);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundCleanup);
    }

    private void OnRoundStarting(RoundStartingEvent args)
    {
        _baseline = Count(out _);
    }

    private void OnRoundCleanup(RoundRestartCleanupEvent args)
    {
        _baseline.Clear();
        _cached.Clear();
        _cachedAt = TimeSpan.MinValue;
        _history.Clear();
    }

    public EntityCensusEuiState GetState()
    {
        var now = _timing.RealTime;

        if (now - _cachedAt >= CacheAge)
        {
            _cached = Count(out _cachedTotal);
            _cachedAt = now;

            // The baseline is normally taken at round start; fall back to the first census
            // (e.g. a panel opened while the server sat in the lobby).
            if (_baseline.Count == 0)
                _baseline = _cached;

            _history.Enqueue((now, _cached));
            while (_history.Count > 0 && now - _history.Peek().Time > MinuteWindow + CacheAge)
                _history.Dequeue();
        }

        // Oldest retained census that is at least ~a minute old, if any.
        var minuteAvailable = _history.Count > 0 && now - _history.Peek().Time >= MinuteWindow - CacheAge;
        var minuteAgo = minuteAvailable ? _history.Peek().Counts : null;

        var entries = new List<EntityCensusEntry>(MaxEntries);
        foreach (var (proto, count) in _cached.OrderByDescending(kv => kv.Value).Take(MaxEntries))
        {
            entries.Add(new EntityCensusEntry(
                proto,
                count,
                count - _baseline.GetValueOrDefault(proto),
                minuteAgo == null ? 0 : count - minuteAgo.GetValueOrDefault(proto)));
        }

        return new EntityCensusEuiState(entries, _cachedTotal, minuteAvailable);
    }

    private Dictionary<string, int> Count(out int total)
    {
        var counts = new Dictionary<string, int>();
        total = 0;

        var query = EntityQueryEnumerator<MetaDataComponent>();
        while (query.MoveNext(out _, out var meta))
        {
            total += 1;
            if (meta.EntityPrototype?.ID is not { } id)
                continue;

            counts[id] = counts.GetValueOrDefault(id) + 1;
        }

        return counts;
    }
}
