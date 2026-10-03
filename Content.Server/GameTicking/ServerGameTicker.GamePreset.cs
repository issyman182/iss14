using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Content.Shared.CCVar;
using Content.Shared.GameTicking.Prototypes;
using Content.Shared.Maps;
using Robust.Shared.Player;

namespace Content.Server.GameTicking;

public sealed partial class ServerGameTicker
{
    // iss14: was 30 seconds. Failed presets now always do a clean delayed restart (see StartPreset),
    // so keep the wait short.
    public const float PresetFailedCooldownIncrease = 10f;

    // iss14: upstream attempts the fallback presets in the same tick when the picked preset fails.
    // Rules of the failed preset that were already started kept running alongside the fallback mode
    // (multiple game modes at once), so this is disabled in favour of a clean delayed restart.
    // Set to true to restore the upstream same-tick fallback behaviour.
    private static readonly bool UseSameTickFallback = false;

    /// <summary>
    /// The selected preset that will be used at the start of the next round.
    /// </summary>
    public GamePresetPrototype? Preset { get; private set; }

    /// <summary>
    /// The selected preset that will be shown at the lobby screen to fool players.
    /// </summary>
    public GamePresetPrototype? Decoy { get; private set; }

    /// <summary>
    /// The preset that's currently active.
    /// </summary>
    public GamePresetPrototype? CurrentPreset { get; private set; }

    /// <summary>
    /// Countdown to the preset being reset to the server default.
    /// </summary>
    public int? ResetCountdown;

    // iss14: fallback gamemode handling (see VoteConfigManager fallback settings).
    // Failed start attempts in a row. Only reset when a non-fallback gamemode starts, so after a fallback round
    // a gamemode that still can't start falls back again right away instead of failing twice more.
    private int _presetFailStreak;
    // Gamemodes that failed during the current streak; skipped when re-rolling from the active gamemode profile.
    private readonly HashSet<string> _failedPresets = new();
    // Fallback gamemode to switch to on the next round restart.
    private string? _pendingFallbackPreset;

    private bool StartPreset(ICommonSession[] origReadyPlayers, bool force)
    {
        Log.Info($"Attempting to start preset '{CurrentPreset?.ID}'");
        var startAttempt = new RoundStartAttemptEvent(origReadyPlayers, force);
        RaiseLocalEvent(startAttempt);

        if (!startAttempt.Cancelled)
        {
            OnPresetStarted();
            return true;
        }

        var presetTitle = CurrentPreset != null ? Loc.GetString(CurrentPreset.ModeTitle) : string.Empty;

        void FailedPresetRestart()
        {
            SendServerMessage(Loc.GetString("game-ticker-start-round-cannot-start-game-mode-restart",
                ("failedGameMode", presetTitle)));
            RestartRound();
            DelayStart(TimeSpan.FromSeconds(PresetFailedCooldownIncrease));
        }

        if (UseSameTickFallback && Cfg.GetCVar(CCVars.GameLobbyFallbackEnabled))
        {
            var fallbackPresets = Cfg.GetCVar(CCVars.GameLobbyFallbackPreset).Split(",");
            var startFailed = true;

            Log.Info($"Fallback - Failed to start round, attempting to start fallback presets.");
            foreach (var preset in fallbackPresets)
            {
                Log.Info($"Fallback - Clearing up gamerules");
                ClearGameRules();
                Log.Info($"Fallback - Attempting to start '{preset}'");
                SetGamePreset(preset, resetDelay: 1);
                AddGamePresetRules();
                StartGamePresetRules();

                startAttempt.Uncancel();
                RaiseLocalEvent(startAttempt);

                if (!startAttempt.Cancelled)
                {
                    _chatManager.SendAdminAnnouncement(
                        Loc.GetString("game-ticker-start-round-cannot-start-game-mode-fallback",
                            ("failedGameMode", presetTitle),
                            ("fallbackMode", Loc.GetString(preset))));
                    RefreshLateJoinAllowed();
                    startFailed = false;
                    break;
                }
                Log.Info($"Fallback - '{preset}' failed to start.");
            }

            if (startFailed)
            {
                FailedPresetRestart();
                return false;
            }
        }

        else
        {
            // iss14: clean restart instead of same-tick fallback; delayed by PresetFailedCooldownIncrease.
            Log.Info($"Failed to start preset '{CurrentPreset?.ID}'. Restarting round with a {PresetFailedCooldownIncrease} second delay.");

            _presetFailStreak++;
            if (CurrentPreset != null)
                _failedPresets.Add(CurrentPreset.ID);

            // Failed too often in a row: switch to the admin fallback gamemode (voting config) on the restart.
            if (TryQueueFallbackPreset(presetTitle))
            {
                RestartRound();
                DelayStart(TimeSpan.FromSeconds(PresetFailedCooldownIncrease));
                return false;
            }

            FailedPresetRestart();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Queues the fallback gamemode for the next round once the failure streak is long enough.
    /// Returns false (normal failed-preset restart) when no fallback applies.
    /// </summary>
    private bool TryQueueFallbackPreset(string failedTitle)
    {
        if (_presetFailStreak < Administration.VoteConfigManager.FallbackAfterFailures)
            return false;

        if (!_voteConfig.TryGetFallbackPreset(out var fallback))
            return false;

        if (fallback.ID == CurrentPreset?.ID)
        {
            // The fallback itself can't start; nothing more we can do automatically.
            _chatManager.SendAdminAnnouncement(Loc.GetString("game-ticker-start-round-fallback-failed",
                ("fallbackMode", failedTitle)));
            return false;
        }

        _pendingFallbackPreset = fallback.ID;
        var message = Loc.GetString("game-ticker-start-round-cannot-start-game-mode-use-fallback",
            ("failedGameMode", failedTitle),
            ("count", _presetFailStreak),
            ("fallbackMode", Loc.GetString(fallback.ModeTitle)));
        Log.Info(message);
        SendServerMessage(message);
        return true;
    }

    private void OnPresetStarted()
    {
        // A fallback round doesn't end the streak: the normal gamemode gets one try next round and
        // falls back again immediately if it still can't start.
        if (CurrentPreset != null && CurrentPreset.ID == _voteConfig.FallbackPreset)
            return;

        _presetFailStreak = 0;
        _failedPresets.Clear();
    }

    [Dependency] private Administration.VoteConfigManager _voteConfig = default!;

    private void InitializeGamePreset()
    {
        // If an admin gamemode profile is active, start a random gamemode from it instead of the default.
        // Gamemodes that just failed to start are skipped if the profile has others ("try the next one").
        if (_voteConfig.TryGetRandomPreset(out var randomPreset, _failedPresets))
        {
            SetGamePreset(randomPreset);
            return;
        }

        SetGamePreset(LobbyEnabled ? Cfg.GetCVar(CCVars.GameLobbyDefaultPreset) : "sandbox");
    }

    public void SetGamePreset(GamePresetPrototype? preset, bool force = false, GamePresetPrototype? decoy = null, int? resetDelay = null)
    {
        // Do nothing if this game ticker is a dummy!
        if (DummyTicker)
            return;

        if (resetDelay is not null)
        {
            ResetCountdown = resetDelay.Value;
            // Reset counter is checked and changed at the end of each round
            // So if the game is in the lobby, the first requested round will happen before the check, and we need one less check
            if (CurrentPreset is null)
                ResetCountdown = resetDelay.Value - 1;
        }
        else
        {
            ResetCountdown = null;
        }

        Preset = preset;
        Decoy = decoy;
        ValidateMap();
        UpdateInfoText();

        if (force)
        {
            StartRound(true);
        }
    }

    public void SetGamePreset(string preset, bool force = false, int? resetDelay = null)
    {
        var proto = FindGamePreset(preset);
        if (proto != null)
            SetGamePreset(proto, force, null, resetDelay);
    }

    public GamePresetPrototype? FindGamePreset(string preset)
    {
        if (ProtoMan.TryIndex(preset, out GamePresetPrototype? presetProto))
            return presetProto;

        foreach (var proto in ProtoMan.EnumeratePrototypes<GamePresetPrototype>())
        {
            foreach (var alias in proto.Alias)
            {
                if (preset.Equals(alias, StringComparison.InvariantCultureIgnoreCase))
                    return proto;
            }
        }

        return null;
    }

    public bool TryFindGamePreset(string preset, [NotNullWhen(true)] out GamePresetPrototype? prototype)
    {
        prototype = FindGamePreset(preset);

        return prototype != null;
    }

    public bool IsMapEligible(GameMapPrototype map)
    {
        if (Preset == null)
            return true;

        if (Preset.MapPool == null || !ProtoMan.TryIndex<GameMapPoolPrototype>(Preset.MapPool, out var pool))
            return true;

        return pool.Maps.Contains(map.ID);
    }

    private void ValidateMap()
    {
        if (Preset == null || _gameMapManager.GetSelectedMap() is not { } map)
            return;

        if (Preset.MapPool == null ||
            !ProtoMan.TryIndex<GameMapPoolPrototype>(Preset.MapPool, out var pool))
            return;

        if (pool.Maps.Contains(map.ID))
            return;

        _gameMapManager.SelectMapRandom();
    }

    private bool AddGamePresetRules()
    {
        if (DummyTicker || Preset == null)
            return false;

        CurrentPreset = Preset;
        foreach (var rule in Preset.Rules)
        {
            AddGameRule(rule);
        }

        return true;
    }

    private void TryResetPreset()
    {
        // iss14: a gamemode failed too often; play the fallback gamemode for one round, then resume the
        // normal selection (ResetCountdown 0 = reset at the next restart).
        if (_pendingFallbackPreset != null)
        {
            var fallback = _pendingFallbackPreset;
            _pendingFallbackPreset = null;
            if (FindGamePreset(fallback) is { } fallbackProto)
            {
                SetGamePreset(fallbackProto);
                ResetCountdown = 0;
                return;
            }
        }

        // iss14 fix: when an admin gamemode profile is active, re-roll a random preset from it every
        // round restart (previously it was only rolled once at server start). An admin-set preset with a
        // pending reset countdown (setgamepreset <rounds>) still takes precedence until it expires.
        if (ResetCountdown is null)
        {
            if (_voteConfig.GetActivePresetSet() != null)
                InitializeGamePreset();
            return;
        }

        if (ResetCountdown-- > 0)
            return;

        InitializeGamePreset();
        ResetCountdown = null;
    }

    public void StartGamePresetRules()
    {
        // May be touched by the preset during init.
        var rules = new List<EntityUid>(GetAddedGameRules());
        foreach (var rule in rules)
        {
            StartGameRule(rule);
        }
    }

    private void IncrementRoundNumber()
    {
        var playerIds = _playerGameStatuses.Keys.Select(player => player.UserId).ToArray();

        // TODO FIXME AAAAAAAAAAAAAAAAAAAH THIS IS BROKEN
        // Task.Run as a terrible dirty workaround to avoid synchronization context deadlock from .Result here.
        // This whole setup logic should be made asynchronous so we can properly wait on the DB AAAAAAAAAAAAAH
        var task = Task.Run(async () =>
        {
            var server = await _dbEntryManager.ServerEntity;
            return await _db.AddNewRound(server, playerIds);
        });

        _taskManager.BlockWaitOnTask(task);
        RoundId = task.GetAwaiter().GetResult();
    }
}
