using System.Linq;
using Content.Server.Chat.Managers;
using Content.Shared.CCVar;
using Robust.Server;
using Robust.Server.Player;
using Robust.Server.ServerStatus;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server.ServerUpdates;

/// <summary>
/// Responsible for restarting the server periodically or for update, when not disruptive.
/// </summary>
/// <remarks>
/// This was originally only designed for restarting on *update*,
/// but now also handles periodic restarting to keep server uptime via <see cref="CCVars.ServerUptimeRestartMinutes"/>.
/// </remarks>
public sealed partial class ServerUpdateManager : IPostInjectInit
{
    [Dependency] private IGameTiming _gameTiming = default!;
    [Dependency] private IWatchdogApi _watchdog = default!;
    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private IChatManager _chatManager = default!;
    [Dependency] private IBaseServer _server = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private ILogManager _logManager = default!;

    private ISawmill _sawmill = default!;

    [ViewVariables]
    private bool _updateOnRoundEnd;

    private TimeSpan? _restartTime;

    private TimeSpan _uptimeRestart;

    private int _roundsBeforeRestart;

    [ViewVariables]
    private int _roundsCompleted;

    // iss14: scheduled daily restart at a configured time of day (autorestartconfig admin window).
    private bool _autoRestartEnabled;
    private TimeSpan? _autoRestartTimeOfDay;
    [ViewVariables]
    private bool _autoRestartPending;
    [ViewVariables]
    private DateTime? _nextAutoRestart;

    /// <summary>iss14: Whether the scheduled restart time has been reached and a restart will happen at round end.</summary>
    public bool AutoRestartPending => _autoRestartPending;

    /// <summary>iss14: The next scheduled restart moment (server local time), or null if disabled/unset.</summary>
    public DateTime? NextAutoRestart => _autoRestartEnabled ? _nextAutoRestart : null;

    public void Initialize()
    {
        _watchdog.UpdateReceived += WatchdogOnUpdateReceived;
        _playerManager.PlayerStatusChanged += PlayerManagerOnPlayerStatusChanged;

        _cfg.OnValueChanged(
            CCVars.ServerUptimeRestartMinutes,
            minutes => _uptimeRestart = TimeSpan.FromMinutes(minutes),
            true);

        _cfg.OnValueChanged(
            CCVars.ServerRoundsBeforeRestart,
            rounds => _roundsBeforeRestart = rounds,
            true);

        // iss14: scheduled daily restart
        _cfg.OnValueChanged(
            CCVars.ServerAutoRestartEnabled,
            enabled =>
            {
                _autoRestartEnabled = enabled;
                if (!enabled)
                    _autoRestartPending = false;
                RecomputeNextAutoRestart();
            },
            true);

        _cfg.OnValueChanged(
            CCVars.ServerAutoRestartTime,
            time =>
            {
                _autoRestartTimeOfDay = Administration.AutoRestartManager.TryParseTimeOfDay(time, out var parsed)
                    ? parsed
                    : null;
                _autoRestartPending = false;
                RecomputeNextAutoRestart();
            },
            true);
    }

    public void Update()
    {
        // iss14: scheduled daily restart - mark pending once the configured time of day passes.
        if (_autoRestartEnabled && !_autoRestartPending && _nextAutoRestart != null && DateTime.Now >= _nextAutoRestart)
        {
            _autoRestartPending = true;
            _sawmill.Info("Scheduled restart time reached, restarting at round end.");
            _chatManager.DispatchServerAnnouncement(Loc.GetString("server-updates-scheduled-restart-pending"));
            ServerEmptyUpdateRestartCheck("scheduled restart time");
        }

        if (_restartTime != null)
        {
            if (_restartTime < _gameTiming.RealTime)
            {
                DoShutdown();
            }
        }
        else
        {
            if (ShouldShutdownDueToUptime())
            {
                ServerEmptyUpdateRestartCheck("uptime");
            }
        }
    }

    /// <summary>
    /// Notify that the round just ended, which is a great time to restart if necessary!
    /// </summary>
    /// <returns>True if the server is going to restart.</returns>
    public bool RoundEnded()
    {
        _roundsCompleted += 1;

        if (_updateOnRoundEnd || ShouldShutdownDueToUptime() || ShouldShutdownDueToRounds() || _autoRestartPending) // iss14: scheduled restart
        {
            DoShutdown();
            return true;
        }

        return false;
    }

    private void PlayerManagerOnPlayerStatusChanged(object? sender, SessionStatusEventArgs e)
    {
        switch (e.NewStatus)
        {
            case SessionStatus.Connected:
                if (_restartTime != null)
                    _sawmill.Debug("Aborting server restart timer due to player connection");

                _restartTime = null;
                break;
            case SessionStatus.Disconnected:
                ServerEmptyUpdateRestartCheck("last player disconnect");
                break;
        }
    }

    private void WatchdogOnUpdateReceived()
    {
        _chatManager.DispatchServerAnnouncement(Loc.GetString("server-updates-received"));
        _updateOnRoundEnd = true;
        ServerEmptyUpdateRestartCheck("update notification");
    }

    /// <summary>
    ///     Checks whether there are still players on the server,
    /// and if not starts a timer to automatically reboot the server if an update is available.
    /// </summary>
    private void ServerEmptyUpdateRestartCheck(string reason)
    {
        // Can't simple check the current connected player count since that doesn't update
        // before PlayerStatusChanged gets fired.
        // So in the disconnect handler we'd still see a single player otherwise.
        var playersOnline = _playerManager.Sessions.Any(p => p.Status != SessionStatus.Disconnected);
        if (playersOnline || !(_updateOnRoundEnd || ShouldShutdownDueToUptime() || _autoRestartPending)) // iss14: scheduled restart
        {
            // Still somebody online.
            return;
        }

        if (_restartTime != null)
        {
            // Do nothing because we already have a timer running.
            return;
        }

        var restartDelay = TimeSpan.FromSeconds(_cfg.GetCVar(CCVars.UpdateRestartDelay));
        _restartTime = restartDelay + _gameTiming.RealTime;

        _sawmill.Debug("Started server-empty restart timer due to {Reason}", reason);
    }

    private void DoShutdown()
    {
        _sawmill.Debug($"Shutting down via {nameof(ServerUpdateManager)}!");
        string reason;
        if (_updateOnRoundEnd)
            reason = "server-updates-shutdown";
        else if (ShouldShutdownDueToRounds())
            reason = "server-updates-shutdown-rounds";
        else if (_autoRestartPending) // iss14: scheduled restart
            reason = "server-updates-shutdown-scheduled";
        else
            reason = "server-updates-shutdown-uptime";
        _server.Shutdown(Loc.GetString(reason));
    }

    /// <summary>iss14: Computes the next occurrence (server local time) of the configured restart time of day.</summary>
    private void RecomputeNextAutoRestart()
    {
        if (_autoRestartTimeOfDay is not { } timeOfDay)
        {
            _nextAutoRestart = null;
            return;
        }

        var now = DateTime.Now;
        var candidate = now.Date + timeOfDay;
        if (candidate <= now)
            candidate = candidate.AddDays(1);

        _nextAutoRestart = candidate;
    }

    private bool ShouldShutdownDueToUptime()
    {
        return _uptimeRestart != TimeSpan.Zero && _gameTiming.RealTime > _uptimeRestart;
    }

    private bool ShouldShutdownDueToRounds()
    {
        return _roundsBeforeRestart > 0 && _roundsCompleted >= _roundsBeforeRestart;
    }

    void IPostInjectInit.PostInject()
    {
        _sawmill = _logManager.GetSawmill("restart");
    }
}
