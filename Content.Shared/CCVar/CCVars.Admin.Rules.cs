using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    /// <summary>
    ///     Time that players have to wait before rules can be accepted.
    /// </summary>
    public static readonly CVarDef<float> RulesWaitTime =
        CVarDef.Create("rules.time", 45f, CVar.SERVER | CVar.REPLICATED);

    /// <summary>
    ///     Don't show rules to localhost/loopback interface.
    /// </summary>
    public static readonly CVarDef<bool> RulesExemptLocal =
        CVarDef.Create("rules.exempt_local", true, CVar.SERVERONLY);

    /// <summary>
    ///     The date the server rules were last changed, e.g. "2026-06-20" (a full timestamp also works).
    ///     Any player who last accepted the rules before this date will have the rules popup shown again
    ///     (with the wait timer) the next time they connect, just like a first-time join.
    ///     Bump this whenever you make a meaningful change to the rules.
    ///     Leave empty to disable version-based re-prompting (only the normal cooldown applies).
    /// </summary>
    public static readonly CVarDef<string> RulesLastUpdated =
        CVarDef.Create("rules.last_updated", "", CVar.SERVERONLY);
}
