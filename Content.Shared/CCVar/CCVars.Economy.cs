// iss14: Economy
using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    /// <summary>
    ///     Hard cap on any single bank balance. Credits above the cap are discarded.
    /// </summary>
    public static readonly CVarDef<long> EconomyMaxBalance =
        CVarDef.Create("economy.max_balance", 1_000_000L, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Whether players are paid a salary for the time they spent in a job this round.
    /// </summary>
    public static readonly CVarDef<bool> EconomySalaryEnabled =
        CVarDef.Create("economy.salary_enabled", true, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Credits per hour for jobs that are not listed in the salary table.
    /// </summary>
    public static readonly CVarDef<int> EconomySalaryDefaultPerHour =
        CVarDef.Create("economy.salary_default_per_hour", 30, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Multiplier applied to every salary payout.
    /// </summary>
    public static readonly CVarDef<float> EconomySalaryMultiplier =
        CVarDef.Create("economy.salary_multiplier", 1.0f, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     When true, an ATM only pays out if the fingerprint of the person using it matches the fingerprint
    ///     stamped on the inserted ID card.
    /// </summary>
    public static readonly CVarDef<bool> EconomyAtmRequireFingerprint =
        CVarDef.Create("economy.atm_require_fingerprint", false, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Maximum credits a single player may withdraw from ATMs per round. 0 disables the limit.
    /// </summary>
    public static readonly CVarDef<int> EconomyAtmWithdrawLimitPerRound =
        CVarDef.Create("economy.atm_withdraw_limit_per_round", 5000, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Fee (percent of the withdrawn amount) the ATM keeps on withdrawal.
    /// </summary>
    public static readonly CVarDef<float> EconomyAtmFeePercent =
        CVarDef.Create("economy.atm_fee_percent", 0f, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     When true, vending machines charge the account of the user's ID card. Replicated so the client can show prices.
    /// </summary>
    public static readonly CVarDef<bool> EconomyVendingPrices =
        CVarDef.Create("economy.vending_prices", false, CVar.SERVER | CVar.REPLICATED | CVar.ARCHIVE);

    /// <summary>
    ///     Multiplier applied to an item's static price when it is vended. The result is rounded and never below 1.
    /// </summary>
    public static readonly CVarDef<float> EconomyVendingPriceMultiplier =
        CVarDef.Create("economy.vending_price_multiplier", 1.0f, CVar.SERVER | CVar.REPLICATED | CVar.ARCHIVE);

    /// <summary>
    ///     Credits one poker chip is worth at the chip exchanger.
    /// </summary>
    public static readonly CVarDef<int> EconomyChipRate =
        CVarDef.Create("economy.chip_rate", 1, CVar.SERVERONLY | CVar.ARCHIVE);
}
