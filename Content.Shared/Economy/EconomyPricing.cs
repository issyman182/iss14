// iss14: Economy
namespace Content.Shared.Economy;

/// <summary>Pure price math shared by the server (charging) and the client (display).</summary>
public static class EconomyPricing
{
    /// <summary>
    /// Credits a vending machine charges for an item with the given static price.
    /// Items without a positive static price are free (0); everything else costs at least 1.
    /// </summary>
    public static int VendPrice(double staticPrice, float multiplier)
    {
        if (staticPrice <= 0)
            return 0;

        var price = (int) Math.Round(staticPrice * multiplier, MidpointRounding.AwayFromZero);
        return Math.Max(1, price);
    }

    /// <summary>
    /// Salary for <paramref name="minutes"/> of job playtime at <paramref name="creditsPerHour"/>,
    /// scaled by <paramref name="multiplier"/> and truncated to whole credits.
    /// </summary>
    public static long Salary(double minutes, int creditsPerHour, float multiplier)
    {
        if (minutes <= 0 || creditsPerHour <= 0 || multiplier <= 0)
            return 0;

        return (long) Math.Floor(minutes * creditsPerHour / 60.0 * multiplier);
    }
}
