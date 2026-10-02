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
    ///     Per-round base salary for jobs that are not listed in the salary table.
    /// </summary>
    public static readonly CVarDef<int> EconomySalaryDefault =
        CVarDef.Create("economy.salary_default", 60, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Fraction of the base salary paid to someone who was present for (almost) none of the round.
    /// </summary>
    public static readonly CVarDef<float> EconomySalaryMinFactor =
        CVarDef.Create("economy.salary_min_factor", 0.2f, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Share of the round duration one has to be present for to receive the full base salary (factor 1.0).
    /// </summary>
    public static readonly CVarDef<float> EconomySalaryFullTimeFraction =
        CVarDef.Create("economy.salary_full_time_fraction", 0.75f, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Factor paid to someone present for the entire round (the bonus; 1.0 disables it).
    /// </summary>
    public static readonly CVarDef<float> EconomySalaryMaxFactor =
        CVarDef.Create("economy.salary_max_factor", 1.5f, CVar.SERVERONLY | CVar.ARCHIVE);

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

    /// <summary>
    ///     Fee (percent) the chip exchanger keeps when turning chips into cash.
    /// </summary>
    public static readonly CVarDef<float> EconomyChipExchangeFeePercent =
        CVarDef.Create("economy.chip_exchange_fee_percent", 5f, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Maximum credits one player may cash out at chip exchangers per round. 0 disables the limit.
    /// </summary>
    public static readonly CVarDef<int> EconomyChipPayoutLimitPerRound =
        CVarDef.Create("economy.chip_payout_limit_per_round", 20000, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Multiplier applied to an item's appraised price for the default price at cash registers.
    /// </summary>
    public static readonly CVarDef<float> EconomyServicePriceMultiplier =
        CVarDef.Create("economy.service_price_multiplier", 1.0f, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Share (percent) of a cash register sale that goes to the cashier; the rest goes to the register's department.
    /// </summary>
    public static readonly CVarDef<float> EconomyServiceCommissionPercent =
        CVarDef.Create("economy.service_commission_percent", 20f, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Whether crew may pay cargo orders from their own bank account.
    /// </summary>
    public static readonly CVarDef<bool> EconomyCargoPrivateOrders =
        CVarDef.Create("economy.cargo_private_orders", true, CVar.SERVER | CVar.REPLICATED | CVar.ARCHIVE);

    /// <summary>
    ///     Price multiplier for privately paid cargo orders.
    /// </summary>
    public static readonly CVarDef<float> EconomyCargoPrivateMarkup =
        CVarDef.Create("economy.cargo_private_markup", 1.5f, CVar.SERVER | CVar.REPLICATED | CVar.ARCHIVE);

    /// <summary>
    ///     Whether the (per-round, cargo-editable) blocklist for private orders is enforced at all.
    /// </summary>
    public static readonly CVarDef<bool> EconomyCargoPrivateBlocklist =
        CVarDef.Create("economy.cargo_private_blocklist", true, CVar.SERVER | CVar.REPLICATED | CVar.ARCHIVE);

    /// <summary>
    ///     Share (percent) of a fine a lawyer receives when paying it for a client.
    /// </summary>
    public static readonly CVarDef<float> EconomyLawyerFeePercent =
        CVarDef.Create("economy.lawyer_fee_percent", 10f, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Whether unpaid fines are deducted from the salary at round end.
    /// </summary>
    public static readonly CVarDef<bool> EconomyFinesFromSalary =
        CVarDef.Create("economy.fines_from_salary", true, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Whether antagonists are paid for cash and valuables they carry off on the evacuation shuttle.
    /// </summary>
    public static readonly CVarDef<bool> EconomyHeistEnabled =
        CVarDef.Create("economy.heist_enabled", true, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Credits paid per credit of appraised value an antagonist escapes with.
    /// </summary>
    public static readonly CVarDef<float> EconomyHeistPayoutFactor =
        CVarDef.Create("economy.heist_payout_factor", 1.0f, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Items below this appraised value don't count as heist loot (cash always counts). Keeps standard gear out.
    /// </summary>
    public static readonly CVarDef<int> EconomyHeistMinItemValue =
        CVarDef.Create("economy.heist_min_item_value", 100, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Bonus credits a thief receives per completed steal objective.
    /// </summary>
    public static readonly CVarDef<int> EconomyHeistThiefBonus =
        CVarDef.Create("economy.heist_thief_bonus", 500, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Whether the store (titles, loadout unlocks) can be used.
    /// </summary>
    public static readonly CVarDef<bool> EconomyStoreEnabled =
        CVarDef.Create("economy.store_enabled", true, CVar.SERVER | CVar.REPLICATED | CVar.ARCHIVE);

    /// <summary>
    ///     Whether department task boards generate tasks.
    /// </summary>
    public static readonly CVarDef<bool> EconomyTasksEnabled =
        CVarDef.Create("economy.tasks_enabled", true, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Multiplier on all task rewards.
    /// </summary>
    public static readonly CVarDef<float> EconomyTaskRewardMultiplier =
        CVarDef.Create("economy.task_reward_multiplier", 1.0f, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Seconds between two generated tasks on one board.
    /// </summary>
    public static readonly CVarDef<int> EconomyTaskIntervalSeconds =
        CVarDef.Create("economy.task_interval_seconds", 600, CVar.SERVERONLY | CVar.ARCHIVE);
}
