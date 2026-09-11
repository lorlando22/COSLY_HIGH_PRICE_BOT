namespace CoslyHighPriceBot.Models;

/// <summary>
/// The same coin (or the same tokenized stock) as seen on every exchange that lists it,
/// built by <see cref="Services.PumpAggregator"/>. <see cref="Quotes"/> is always ordered by
/// exchange priority, so the first entry is the one whose price/open/high/low/volume/trades
/// a message shows and whose primary alias becomes <see cref="Key"/> — unless a lower-
/// priority quote is the only one that actually qualifies (see
/// <see cref="BestQualifyingQuote"/>).
/// </summary>
internal sealed record CoinGroup(string Key, CoinKind Kind, IReadOnlyList<ExchangeQuote> Quotes)
{
    /// <summary>
    /// Every alias contributed by every exchange in the group, deduplicated. Used to look
    /// this group up in the alert history regardless of which exchange's name was on file
    /// from a previous run.
    /// </summary>
    public IReadOnlyList<string> Aliases =>
        Quotes.SelectMany(q => q.Aliases).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// The highest 24h change among quotes that themselves clear <paramref name="threshold"/>,
    /// or null if none does — a group can exist (to show the % on every exchange) without
    /// qualifying for an alert at all.
    /// </summary>
    public decimal? BestQualifyingChangePercent(decimal threshold)
    {
        decimal? best = null;

        foreach (var quote in Quotes)
            if (quote.ChangePercent >= threshold && (best is null || quote.ChangePercent > best))
                best = quote.ChangePercent;

        return best;
    }

    /// <summary>
    /// The highest-priority quote that itself clears <paramref name="threshold"/>: its
    /// price/open/high/low/volume/trade count are what the message shows, since a
    /// non-qualifying exchange's numbers for the same coin can look stale or plain wrong
    /// next to the one that's actually pumping.
    /// </summary>
    public ExchangeQuote? BestQualifyingQuote(decimal threshold) =>
        Quotes.FirstOrDefault(q => q.ChangePercent >= threshold);
}
