namespace CoslyHighPriceBot.Services;

/// <summary>
/// The technical indicators the early-pump module runs on candles: Wilder's RSI, Bollinger
/// bands and the helpers around them. Pure functions with no I/O, so they can be checked
/// against any charting tool.
/// <para>
/// These work in <c>double</c>, not the <c>decimal</c> used everywhere else: a standard
/// deviation needs a square root, which decimal has no operator for. Prices reach down to
/// 0.00000001 and double carries 15+ significant digits, so the precision lost is far below
/// what any threshold here can distinguish.
/// </para>
/// </summary>
internal static class Indicators
{
    /// <summary>One point of a Bollinger band series.</summary>
    internal readonly record struct BollingerPoint(double Middle, double Upper, double Lower)
    {
        /// <summary>
        /// Band width normalised by the middle band, so it is comparable across symbols of
        /// wildly different prices. This is the number the squeeze test looks at.
        /// </summary>
        public double Width => Middle == 0d ? 0d : (Upper - Lower) / Middle;
    }

    /// <summary>
    /// Bollinger bands over a moving window, aligned with <paramref name="closes"/>. Entries
    /// before the window is full are null. Uses the population standard deviation, which is
    /// what charting platforms draw.
    /// </summary>
    public static BollingerPoint?[] Bollinger(IReadOnlyList<double> closes, int period, double stdDevMultiplier)
    {
        var result = new BollingerPoint?[closes.Count];

        for (var i = period - 1; i < closes.Count; i++)
        {
            var mean = 0d;
            for (var j = i - period + 1; j <= i; j++)
                mean += closes[j];
            mean /= period;

            var variance = 0d;
            for (var j = i - period + 1; j <= i; j++)
            {
                var delta = closes[j] - mean;
                variance += delta * delta;
            }

            var deviation = Math.Sqrt(variance / period);
            result[i] = new BollingerPoint(mean, mean + stdDevMultiplier * deviation, mean - stdDevMultiplier * deviation);
        }

        return result;
    }

    /// <summary>
    /// Wilder's RSI, aligned with <paramref name="closes"/>: a simple average over the first
    /// <paramref name="period"/> changes, then smoothed as <c>(previous * (n - 1) + current) / n</c>.
    /// Entries before the warm-up are null. A window with no losses returns 100.
    /// </summary>
    public static double?[] Rsi(IReadOnlyList<double> closes, int period)
    {
        var result = new double?[closes.Count];

        if (closes.Count <= period)
            return result;

        var gains = 0d;
        var losses = 0d;
        for (var i = 1; i <= period; i++)
        {
            var change = closes[i] - closes[i - 1];
            if (change >= 0d)
                gains += change;
            else
                losses -= change;
        }

        var averageGain = gains / period;
        var averageLoss = losses / period;
        result[period] = Value(averageGain, averageLoss);

        for (var i = period + 1; i < closes.Count; i++)
        {
            var change = closes[i] - closes[i - 1];
            averageGain = (averageGain * (period - 1) + Math.Max(change, 0d)) / period;
            averageLoss = (averageLoss * (period - 1) + Math.Max(-change, 0d)) / period;
            result[i] = Value(averageGain, averageLoss);
        }

        return result;

        static double Value(double averageGain, double averageLoss) =>
            averageLoss == 0d ? 100d : 100d - 100d / (1d + averageGain / averageLoss);
    }

    /// <summary>
    /// Simple moving average of the <paramref name="period"/> values ending at
    /// <paramref name="lastIndex"/> inclusive. Returns null when there isn't enough history.
    /// </summary>
    public static double? Sma(IReadOnlyList<double> values, int period, int lastIndex)
    {
        if (period <= 0 || lastIndex < period - 1 || lastIndex >= values.Count)
            return null;

        var sum = 0d;
        for (var i = lastIndex - period + 1; i <= lastIndex; i++)
            sum += values[i];

        return sum / period;
    }

    /// <summary>
    /// Nearest-rank percentile: the value sitting at <paramref name="percentile"/> of the
    /// sorted sample, where 0.20 means "only a fifth of the sample is tighter than this".
    /// </summary>
    public static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        var sorted = values.Order().ToArray();
        var index = Math.Clamp((int)(sorted.Length * percentile), 0, sorted.Length - 1);
        return sorted[index];
    }

    /// <summary>
    /// What fraction of <paramref name="values"/> sits strictly below <paramref name="value"/>,
    /// as 0-100. Used to report how tight a squeeze actually was, not just that it passed.
    /// </summary>
    public static double Rank(IReadOnlyList<double> values, double value)
    {
        if (values.Count == 0)
            return 0d;

        var below = values.Count(v => v < value);
        return below * 100d / values.Count;
    }
}
