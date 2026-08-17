using System.Globalization;
using CoslyHighPriceBot.Models;

namespace CoslyHighPriceBot.Services;

internal static class CoinFilter
{
    /// <summary>
    /// Keeps the pairs for the given quote asset that rose at least as much as the
    /// threshold for their kind, sorted from highest to lowest gain. Tokenized stocks
    /// move far less than crypto, so they get their own (lower) threshold.
    /// </summary>
    public static IReadOnlyList<Coin> Filter(
        IEnumerable<Ticker24h> tickers,
        string quoteAsset,
        decimal minChangePercent,
        decimal stockMinChangePercent,
        IReadOnlySet<string> tokenizedStockAssets)
    {
        var matches = new List<Coin>();

        foreach (var ticker in tickers)
        {
            if (!ticker.Symbol.EndsWith(quoteAsset, StringComparison.Ordinal))
                continue;

            // A symbol that's just the quote asset (or has non-numeric values) is of no use to us.
            if (ticker.Symbol.Length <= quoteAsset.Length)
                continue;

            if (!TryParse(ticker.PriceChangePercent, out var changePercent))
                continue;

            var baseAsset = ticker.Symbol[..^quoteAsset.Length];
            var kind = tokenizedStockAssets.Contains(baseAsset) ? CoinKind.TokenizedStock : CoinKind.Crypto;
            var threshold = kind == CoinKind.TokenizedStock ? stockMinChangePercent : minChangePercent;

            if (changePercent < threshold)
                continue;

            matches.Add(new Coin(
                Symbol: ticker.Symbol,
                BaseAsset: baseAsset,
                QuoteAsset: quoteAsset,
                Kind: kind,
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
