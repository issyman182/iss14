using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    /*
     * Server
     */

    /// <summary>
    ///     Change this to have the changelog and rules "last seen" date stored separately.
    /// </summary>
    public static readonly CVarDef<string> ServerId =
        CVarDef.Create("server.id", "unknown_server_id", CVar.REPLICATED | CVar.SERVER);

    /// <summary>
    ///     Guide Entry Prototype ID to be displayed as the server rules.
    /// </summary>
    public static readonly CVarDef<string> RulesFile =
        CVarDef.Create("server.rules_file", "DefaultRuleset", CVar.REPLICATED | CVar.SERVER);

    /// <summary>
    ///     Guide entry that is displayed by default when a guide is opened.
    /// </summary>
    public static readonly CVarDef<string> DefaultGuide =
        CVarDef.Create("server.default_guide", "NewPlayer", CVar.REPLICATED | CVar.SERVER);

    /// <summary>
    ///     If greater than 0, automatically restart the server after this many minutes of uptime.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     This is intended to work around various bugs and performance issues caused by long continuous server uptime.
    /// </para>
    /// <para>
    ///     This uses the same non-disruptive logic as update restarts,
    ///     i.e. the game will only restart at round end or when there is nobody connected.
    /// </para>
    /// </remarks>
    public static readonly CVarDef<int> ServerUptimeRestartMinutes =
        CVarDef.Create("server.uptime_restart_minutes", 0, CVar.SERVERONLY);

    /// <summary>
    ///     If greater than 0, automatically restart the server after this many rounds have ended.
    /// </summary>
    /// <remarks>
    ///     Like <see cref="ServerUptimeRestartMinutes"/>, the shutdown happens non-disruptively at round end.
    ///     The process exits cleanly, so the service manager (systemd, watchdog) must be configured to restart it.
    /// </remarks>
    public static readonly CVarDef<int> ServerRoundsBeforeRestart =
        CVarDef.Create("server.rounds_before_restart", 0, CVar.SERVERONLY);

    /// <summary>
    ///     iss14: If true, automatically restart the server every day at <see cref="ServerAutoRestartTime"/>.
    /// </summary>
    /// <remarks>
    ///     Like <see cref="ServerRoundsBeforeRestart"/>, the shutdown happens non-disruptively at round end
    ///     and the service manager (systemd, watchdog) must be configured to restart the process.
    ///     Configurable in-game via the <c>autorestartconfig</c> admin window.
    /// </remarks>
    public static readonly CVarDef<bool> ServerAutoRestartEnabled =
        CVarDef.Create("server.auto_restart_enabled", false, CVar.SERVERONLY);

    /// <summary>
    ///     iss14: The local server time of day to automatically restart at, in 24h "HH:mm" format (e.g. "03:00").
    ///     Only used when <see cref="ServerAutoRestartEnabled"/> is true.
    /// </summary>
    public static readonly CVarDef<string> ServerAutoRestartTime =
        CVarDef.Create("server.auto_restart_time", "03:00", CVar.SERVERONLY);

    /// <summary>
    ///     iss14: If true, broadcast <see cref="ServerRoundEndAdMessage"/> in chat when a round ends.
    ///     Configurable in-game via the <c>adconfig</c> admin window.
    /// </summary>
    public static readonly CVarDef<bool> ServerRoundEndAdEnabled =
        CVarDef.Create("server.round_end_ad_enabled", false, CVar.SERVERONLY);

    /// <summary>
    ///     iss14: The advertisement message broadcast at round end (e.g. a Discord invite).
    ///     Literal "\n" sequences are converted to line breaks when broadcast.
    /// </summary>
    public static readonly CVarDef<string> ServerRoundEndAdMessage =
        CVarDef.Create("server.round_end_ad_message", "", CVar.SERVERONLY);

    /// <summary>
    ///     This will be the title shown in the lobby
    ///     If empty, the title will be {ui-lobby-title} + the server's full name from the hub
    /// </summary>
    public static readonly CVarDef<string> ServerLobbyName =
        CVarDef.Create("server.lobby_name", "", CVar.REPLICATED | CVar.SERVER);

    /// <summary>
    ///     The width of the right side (chat) panel in the lobby
    /// </summary>
    public static readonly CVarDef<int> ServerLobbyRightPanelWidth =
        CVarDef.Create("server.lobby_right_panel_width", 650, CVar.REPLICATED | CVar.SERVER);

    /// <summary>
    ///     Forces clients to display version watermark, as if HudVersionWatermark was true
    /// </summary>
    public static readonly CVarDef<bool> ForceClientHudVersionWatermark =
        CVarDef.Create("server.force_client_hud_version_watermark", false, CVar.REPLICATED | CVar.SERVER);
}
