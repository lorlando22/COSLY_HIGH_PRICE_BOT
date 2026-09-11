using System.Globalization;

namespace CoslyHighPriceBot.Services;

/// <summary>Small, exchange-agnostic helpers shared by every exchange client and by DailyPumpModule.</summary>
internal static class CoinFilter
{
    /// <summary>
    /// The milestone a coin has reached: the threshold itself, then one every `step` above it
    /// (+100%, +150%, +200%... with a threshold of 100 and a step of 50). A step of 0 — or a
    /// coin below the threshold — collapses to the threshold, which is the old behaviour.
    /// </summary>
    public static decimal Milestone(decimal changePercent, decimal threshold, decimal step)
    {
        if (step <= 0m || changePercent <= threshold)
            return threshold;

        var steps = Math.Floor((changePercent - threshold) / step);
        return threshold + steps * step;
    }

    /// <summary>
    /// Every exchange sends its numeric fields as strings. This is the one place that turns
    /// them into numbers, always with the invariant culture — a machine set to a comma
    /// decimal separator would otherwise read "1.5" as 15.
    /// </summary>
    internal static bool TryParse(string value, out decimal result) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    /// <summary>Same, for fields where an unreadable value is better treated as zero than as an error.</summary>
    internal static decimal Parse(string value) => TryParse(value, out var result) ? result : 0m;
}
