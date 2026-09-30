using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Server.GameTicking;
using Content.Shared.CCVar;
using Content.Shared.Maps;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Server.Maps;

public sealed partial class GameMapManager : IGameMapManager
{
    [Dependency] private IEntityManager _entityManager = default!;
    [Dependency] private IPrototypeManager _prototypeManager = default!;
    [Dependency] private IConfigurationManager _configurationManager = default!;
    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private IResourceManager _resMan = default!;
    [Dependency] private IRobustRandom _random = default!;

    [ViewVariables(VVAccess.ReadOnly)]
    private readonly Queue<string> _previousMaps = new();
    [ViewVariables(VVAccess.ReadOnly)]
    private GameMapPrototype? _configSelectedMap;
    [ViewVariables(VVAccess.ReadOnly)]
    private GameMapPrototype? _selectedMap; // Don't change this value during a round!
    [ViewVariables(VVAccess.ReadOnly)]
    private bool _mapRotationEnabled;
    [ViewVariables(VVAccess.ReadOnly)]
    private int _mapQueueDepth = 1;

    // Admin-defined runtime pool (set by VoteConfigManager); overrides the configured pool when non-null.
    private IReadOnlySet<string>? _runtimeMapPool;
    // When the runtime pool is active, whether its maps are still filtered by player count.
    private bool _runtimeMapPoolFilterByPlayerCount;
    // Admin-defined per-map player-count ranges (set by VoteConfigManager); override prototype MinPlayers/MaxPlayers.
    private IReadOnlyDictionary<string, (int Min, int Max)>? _runtimeMapPlayerRanges;

    private ISawmill _log = default!;

    public void Initialize()
    {
        _log = Logger.GetSawmill("mapsel");

        _configurationManager.OnValueChanged(CCVars.GameMap, value =>
        {
            if (TryLookupMap(value, out GameMapPrototype? map))
            {
                _configSelectedMap = map;
                return;
            }

            if (string.IsNullOrEmpty(value))
            {
                _configSelectedMap = default!;
                return;
            }

            if (_configurationManager.GetCVar<bool>(CCVars.UsePersistence))
            {
                var startMap = _configurationManager.GetCVar<string>(CCVars.PersistenceMap);
                _configSelectedMap = _prototypeManager.Index<GameMapPrototype>(startMap);

                var mapPath = new ResPath(value);
                if (_resMan.UserData.Exists(mapPath))
                {
                    _configSelectedMap = _configSelectedMap.Persistence(mapPath);
                    _log.Info($"Using persistence map from {value}");
                    return;
                }

                // persistence save path doesn't exist so we just use the start map
                _log.Warning($"Using persistence start map {startMap} as {value} doesn't exist");
                return;
            }

            _log.Error($"Unknown map prototype {value} was selected!");
        }, true);
        _configurationManager.OnValueChanged(CCVars.GameMapRotation, value => _mapRotationEnabled = value, true);
        _configurationManager.OnValueChanged(CCVars.GameMapMemoryDepth, value =>
        {
            _mapQueueDepth = value;
            // Drain excess.
            while (_previousMaps.Count > _mapQueueDepth)
            {
                _previousMaps.Dequeue();
            }
        }, true);

        var maps = AllVotableMaps().ToArray();
        _random.Shuffle(maps);
        foreach (var map in maps)
        {
            if (_previousMaps.Count >= _mapQueueDepth)
                break;
            _previousMaps.Enqueue(map.ID);
        }
    }

    public IEnumerable<GameMapPrototype> CurrentlyEligibleMaps()
    {
        // When an admin profile (runtime pool) is active it is authoritative: return exactly those maps,
        // bypassing the usual condition eligibility filtering and the fallback list. Player-count filtering
        // is applied only when the admin opted into it via RuntimeMapPoolFilterByPlayerCount.
        if (_runtimeMapPool != null)
        {
            var pool = AllVotableMaps();
            if (_runtimeMapPoolFilterByPlayerCount)
                pool = pool.Where(IsPlayerCountEligible);

            var arr = pool.ToArray();
            if (arr.Length != 0)
                return arr;
        }

        var maps = AllVotableMaps().Where(IsMapEligible).ToArray();
        return maps.Length == 0 ? AllMaps().Where(x => x.Fallback) : maps;
    }

    public void SetRuntimeMapPool(IReadOnlySet<string>? maps)
    {
        _runtimeMapPool = maps;
    }

    public bool RuntimeMapPoolFilterByPlayerCount
    {
        get => _runtimeMapPoolFilterByPlayerCount;
        set => _runtimeMapPoolFilterByPlayerCount = value;
    }

    public void SetRuntimeMapPlayerRanges(IReadOnlyDictionary<string, (int Min, int Max)>? ranges)
    {
        _runtimeMapPlayerRanges = ranges;
    }

    public IEnumerable<GameMapPrototype> AllVotableMaps()
    {
        // Admin runtime override takes precedence over the configured/preset pool.
        if (_runtimeMapPool != null)
        {
            foreach (var map in _runtimeMapPool)
            {
                if (_prototypeManager.TryIndex<GameMapPrototype>(map, out var overrideProto))
                    yield return overrideProto;
            }

            yield break;
        }

        var poolPrototype = _entityManager.System<ServerGameTicker>().Preset?.MapPool ??
                   _configurationManager.GetCVar(CCVars.GameMapPool);

        if (_prototypeManager.TryIndex(poolPrototype, out var pool))
        {
            foreach (var map in pool.Maps)
            {
                if (!_prototypeManager.TryIndex(map, out var mapProto))
                {
                    _log.Error($"Couldn't index map {map} in pool {poolPrototype}");
                    continue;
                }

                yield return mapProto;
            }
        }
        else
        {
            throw new Exception($"Could not index map pool prototype {poolPrototype}!");
        }
    }

    public IEnumerable<GameMapPrototype> AllMaps()
    {
        return _prototypeManager.EnumeratePrototypes<GameMapPrototype>();
    }

    public GameMapPrototype? GetSelectedMap()
    {
        // When an admin runtime map pool is active it is authoritative: ignore the config-forced map
        // (game.map) so that map votes and random-on-restart selection from the pool take effect.
        // _selectedMap is set by votemap/forcemap and by SelectMapByConfigRules at round start;
        // when null (e.g. just after ClearSelectedMap) the round-start flow will pick from the pool.
        if (_runtimeMapPool != null)
            return _selectedMap;

        return _configSelectedMap ?? _selectedMap;
    }

    public void ClearSelectedMap()
    {
        _selectedMap = default!;
    }

    public bool TrySelectMapIfEligible(string gameMap)
    {
        if (!TryLookupMap(gameMap, out var map) || !IsMapEligible(map))
            return false;
        _selectedMap = map;
        return true;
    }

    public void SelectMap(string gameMap)
    {
        if (!TryLookupMap(gameMap, out var map))
            throw new ArgumentException($"The map \"{gameMap}\" is invalid!");
        _selectedMap = map;
    }

    public void SelectMapRandom()
    {
        var maps = CurrentlyEligibleMaps().ToList();
        _selectedMap = _random.Pick(maps);
    }

    public void SelectMapFromRotationQueue(bool markAsPlayed = false)
    {
        var map = GetFirstInRotationQueue();

        _selectedMap = map;

        if (markAsPlayed)
            EnqueueMap(map.ID);
    }

    public void SelectMapByConfigRules()
    {
        if (_mapRotationEnabled)
        {
            _log.Info("selecting the next map from the rotation queue");
            SelectMapFromRotationQueue(true);
        }
        else
        {
            _log.Info("selecting a random map");
            SelectMapRandom();
        }
    }

    public bool CheckMapExists(string gameMap)
    {
        return TryLookupMap(gameMap, out _);
    }

    private bool IsMapEligible(GameMapPrototype map)
    {
        return IsPlayerCountEligible(map) &&
               map.Conditions.All(x => x.Check(map)) &&
               _entityManager.System<ServerGameTicker>().IsMapEligible(map);
    }

    private bool IsPlayerCountEligible(GameMapPrototype map)
    {
        var count = _playerManager.PlayerCount;

        // Admin-configured range overrides the prototype's MinPlayers/MaxPlayers. Max 0 = unlimited.
        if (_runtimeMapPlayerRanges != null && _runtimeMapPlayerRanges.TryGetValue(map.ID, out var range))
            return count >= range.Min && (range.Max <= 0 || count <= range.Max);

        return map.MaxPlayers >= count &&
               map.MinPlayers <= count;
    }

    private bool TryLookupMap(string gameMap, [NotNullWhen(true)] out GameMapPrototype? map)
    {
        return _prototypeManager.TryIndex(gameMap, out map);
    }

    private int GetMapRotationQueuePriority(string gameMapProtoName)
    {
        var i = 0;
        foreach (var map in _previousMaps.Reverse())
        {
            if (map == gameMapProtoName)
                return i;
            i++;
        }
        return _mapQueueDepth;
    }

    private GameMapPrototype GetFirstInRotationQueue()
    {
        _log.Info($"map queue: {string.Join(", ", _previousMaps)}");

        var eligible = CurrentlyEligibleMaps()
            .Select(x => (proto: x, weight: GetMapRotationQueuePriority(x.ID)))
            .OrderByDescending(x => x.weight)
            .ToArray();

        _log.Info($"eligible queue: {string.Join(", ", eligible.Select(x => (x.proto.ID, x.weight)))}");

        // YML "should" be configured with at least one fallback map
        Debug.Assert(eligible.Length != 0, $"couldn't select a map with {nameof(GetFirstInRotationQueue)}()! No eligible maps and no fallback maps!");

        var weight = eligible[0].weight;
        return eligible.Where(x => x.Item2 == weight)
            .MinBy(x => x.proto.ID)
            .proto;
    }

    private void EnqueueMap(string mapProtoName)
    {
        _previousMaps.Enqueue(mapProtoName);
        while (_previousMaps.Count > _mapQueueDepth)
        {
            _previousMaps.Dequeue();
        }
    }
}
