using System.Globalization;
using CoslyHighPriceBot.Models;

namespace CoslyHighPriceBot.Services;

internal static class CoinFilter
{
    /// <summary>
    /// Keeps the pairs for the given quote asset that rose at least
    /// <paramref name="minChangePercent"/> in 24h, sorted from highest to lowest gain.
    /// </summary>
    public static IReadOnlyList<Coin> Filter(
        IEnumerable<Ticker24h> tickers,
        string quoteAsset,
        decimal minChangePercent)
    {
        var matches = new List<Coin>();

        foreach (var ticker in tickers)
        {
            if (!ticker.Symbol.EndsWith(quoteAsset, StringComparison.Ordinal))
                continue;

            // A symbol that's just the quote asset (or has non-numeric values) is of no use to us.
            if (ticker.Symbol.Length <= quoteAsset.Length)
                continue;

            if (!TryParse(ticker.PriceChangePercent, out var changePercent) || changePercent < minChangePercent)
                continue;

            matches.Add(new Coin(
                Symbol: ticker.Symbol,
                QuoteAsset: quoteAsset,
                ChangePercent: changePercent,
                LastPrice: Parse(ticker.LastPrice),
                OpenPrice: Parse(ticker.OpenPrice),
                HighPrice: Parse(ticker.HighPrice),
                LowPrice: Parse(ticker.LowPrice),
                QuoteVolume: Parse(ticker.QuoteVolume),
                TradeCount: ticker.TradeCount));
        }

        return matches.OrderByDescending(c => c.ChangePercent).ToList();
    }

    /// <summary>Counts how many symbols on the exchange belong to the given quote asset.</summary>
    public static int CountQuotePairs(IEnumerable<Ticker24h> tickers, string quoteAsset) =>
        tickers.Count(t =>
            t.Symbol.Length > quoteAsset.Length &&
            t.Symbol.EndsWith(quoteAsset, StringComparison.Ordinal));

    private static bool TryParse(string value, out decimal result) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    private static decimal Parse(string value) => TryParse(value, out var result) ? result : 0m;
}
