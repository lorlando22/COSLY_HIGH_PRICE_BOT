using CoslyHighPriceBot.Models;

namespace CoslyHighPriceBot.Services;

/// <summary>
/// Merges every exchange's tradeable, classified quotes into cross-exchange groups, so the
/// same coin listed on more than one exchange produces one alert instead of a duplicate per
/// exchange. Static and side-effect free — it doesn't know about thresholds, memory or
/// Telegram, which is what makes it easy to test on its own.
/// </summary>
internal static class PumpAggregator
{
    /// <summary>
    /// How far apart two exchanges' multiplier-adjusted prices for a shared alias are
    /// allowed to be before they're treated as the same coin. Loose on purpose: during a
    /// real pump, prices across exchanges can separate by a few points within seconds, while
    /// two unrelated tickers that happen to share a name differ by orders of magnitude —
    /// BingX's "MEME" token (AMEMECOIN) trades around 100x BingX/Bybit's "MEME" meme coin.
    /// </summary>
    private const decimal MaxPriceRatio = 1.25m;

    /// <summary>
    /// Groups every quote by shared alias and compatible price, never mixing crypto with
    /// tokenized stocks and never putting two quotes from the same exchange in one group.
    /// <paramref name="priority"/> controls two things: the order exchanges are visited in
    /// (so a later exchange joins a group an earlier one already started, not the other way
    /// around) and the order each group's own quotes end up in.
    /// </summary>
    public static IReadOnlyList<CoinGroup> Group(IReadOnlyList<ExchangeQuote> quotes, IReadOnlyList<string> priority)
    {
        var groups = new List<CoinGroup>();

        foreach (var kind in new[] { CoinKind.Crypto, CoinKind.TokenizedStock })
        {
            var ofKind = quotes.Where(q => q.Kind == kind).ToList();
            groups.AddRange(GroupByKind(ofKind, priority));
        }

        return groups;
    }

    private static IReadOnlyList<CoinGroup> GroupByKind(IReadOnlyList<ExchangeQuote> quotes, IReadOnlyList<string> priority)
    {
        var working = new List<List<ExchangeQuote>>();

        foreach (var exchange in priority)
        {
            foreach (var quote in quotes.Where(q => q.ExchangeName == exchange))
            {
                var matches = working
                    .Where(g => SharesAlias(g, quote) && !HasExchange(g, quote.ExchangeName) && IsPriceCompatible(g, quote))
                    .ToList();

                if (matches.Count == 0)
                {
                    working.Add([quote]);
                    continue;
                }

                // Several existing groups can share an alias with this quote (the NEIRO /
                // NEIROCTO / 1000NEIROCTO case): fold them into one, but only when they don't
                // already have a quote from the same exchange as each other — that would mean
                // choosing between two quotes from the same exchange, which never happens.
                var target = matches[0];
                foreach (var other in matches.Skip(1))
                {
                    if (other.Any(o => HasExchange(target, o.ExchangeName)))
                        continue;

                    target.AddRange(other);
                    working.Remove(other);
                }

                target.Add(quote);
            }
        }

        return working.Select(g => BuildGroup(g, priority)).ToList();
    }

    private static CoinGroup BuildGroup(List<ExchangeQuote> quotes, IReadOnlyList<string> priority)
    {
        var ordered = quotes.OrderBy(q => PriorityIndex(q.ExchangeName, priority)).ToList();

        // The group's key is the primary alias of its highest-priority quote, so it stays
        // stable across runs as long as that exchange keeps listing the coin — even if a
        // lower-priority exchange's name for it is what ends up in the alert history (see
        // AlertHistoryStore's alias lookup).
        return new CoinGroup(ordered[0].Aliases[0], ordered[0].Kind, ordered);
    }

    private static int PriorityIndex(string exchangeName, IReadOnlyList<string> priority)
    {
        for (var i = 0; i < priority.Count; i++)
            if (string.Equals(priority[i], exchangeName, StringComparison.Ordinal))
                return i;

        return int.MaxValue;
    }

    private static bool SharesAlias(List<ExchangeQuote> group, ExchangeQuote quote) =>
        group.SelectMany(q => q.Aliases).Intersect(quote.Aliases, StringComparer.Ordinal).Any();

    private static bool HasExchange(List<ExchangeQuote> group, string exchangeName) =>
        group.Any(q => string.Equals(q.ExchangeName, exchangeName, StringComparison.Ordinal));

    /// <summary>
    /// This is what stops two different coins that happen to share a ticker from being
    /// merged just because their names collide: a real cross-exchange quote for the same
    /// coin stays within <see cref="MaxPriceRatio"/>, a name collision between unrelated
    /// assets doesn't.
    /// </summary>
    private static bool IsPriceCompatible(List<ExchangeQuote> group, ExchangeQuote quote)
    {
        var prices = group.Select(q => q.AdjustedPrice).Append(quote.AdjustedPrice).Where(p => p > 0m).ToList();
        return prices.Count == 0 || prices.Max() / prices.Min() <= MaxPriceRatio;
    }
}
