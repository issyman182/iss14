using Content.Shared.Maps;

namespace Content.Server.Maps;

/// <summary>
/// Manages which station map will be used for the next round.
/// </summary>
public interface IGameMapManager
{
    void Initialize();

    /// <summary>
    /// Returns all maps eligible to be played right now.
    /// </summary>
    /// <returns>enumerator of map prototypes</returns>
    IEnumerable<GameMapPrototype> CurrentlyEligibleMaps();

    /// <summary>
    /// Returns all maps that can be voted for.
    /// </summary>
    /// <returns>enumerator of map prototypes</returns>
    IEnumerable<GameMapPrototype> AllVotableMaps();

    /// <summary>
    /// Installs (or clears, when null) an admin-defined runtime map pool that overrides the configured pool for
    /// both random map selection and map voting.
    /// </summary>
    void SetRuntimeMapPool(IReadOnlySet<string>? maps);

    /// <summary>
    /// When an admin runtime map pool is active, controls whether its maps are still filtered by the current
    /// player count. <c>false</c> (default) makes the pool fully authoritative.
    /// </summary>
    bool RuntimeMapPoolFilterByPlayerCount { get; set; }

    /// <summary>
    /// Installs (or clears, when null) admin-defined per-map player-count ranges that override the
    /// MinPlayers/MaxPlayers baked into the map prototypes wherever player-count eligibility is checked.
    /// A Max of 0 means unlimited.
    /// </summary>
    void SetRuntimeMapPlayerRanges(IReadOnlyDictionary<string, (int Min, int Max)>? ranges);

    /// <summary>
    /// Sets the admin fallback map (from VoteConfigManager), used when no map of the pool is currently eligible.
    /// Null disables it, so the prototype-flagged fallback maps are used instead.
    /// </summary>
    void SetRuntimeFallbackMap(string? mapId);

    /// <summary>
    /// Returns all maps.
    /// </summary>
    /// <returns>enumerator of map prototypes</returns>
    IEnumerable<GameMapPrototype> AllMaps();

    /// <summary>
    /// Gets the currently selected map
    /// </summary>
    /// <returns>selected map</returns>
    GameMapPrototype? GetSelectedMap();

    /// <summary>
    /// Clears the selected map, if any
    /// </summary>
    void ClearSelectedMap();

    /// <summary>
    /// Attempts to select the given map, checking eligibility criteria
    /// </summary>
    /// <param name="gameMap">map prototype</param>
    /// <returns>success or failure</returns>
    bool TrySelectMapIfEligible(string gameMap);

    /// <summary>
    /// Select the given map regardless of eligibility
    /// </summary>
    /// <param name="gameMap">map prototype</param>
    /// <returns>success or failure</returns>
    void SelectMap(string gameMap);

    /// <summary>
    /// Selects a random map eligible map
    /// </summary>
    void SelectMapRandom();

    /// <summary>
    /// Selects the map at the front of the rotation queue
    /// </summary>
    /// <returns>selected map</returns>
    void SelectMapFromRotationQueue(bool markAsPlayed = false);

    /// <summary>
    /// Selects the map by following rules set in the config
    /// </summary>
    public void SelectMapByConfigRules();

    /// <summary>
    /// Checks if the given map exists
    /// </summary>
    /// <param name="gameMap">name of the map</param>
    /// <returns>existence</returns>
    bool CheckMapExists(string gameMap);
}
