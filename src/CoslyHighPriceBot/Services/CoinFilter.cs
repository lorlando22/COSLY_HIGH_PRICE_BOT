using System.Globalization;
using CoslyHighPriceBot.Models;

namespace CoslyHighPriceBot.Services;

internal static class CoinFilter
{
    /// <summary>Contract type Binance uses for tokenized equities, commodities and other TradFi instruments.</summary>
    private const string TradFiContractType = "TRADIFI_PERPETUAL";

    /// <summary>
    /// First pass, before anything is known about each symbol's kind: keeps the pairs for
    /// the given quote asset that rose at least <paramref name="minChangePercent"/>, which
    /// must be the lower of the two thresholds. Running this first means the exchangeInfo
    /// call is only paid for when something is actually worth classifying.
    /// </summary>
    public static IReadOnlyList<Coin> FindCandidates(
        IEnumerable<Ticker24h> tickers,
        string quoteAsset,
        decimal minChangePercent)
    {
        var matches = new List<Coin>();

        foreach (var ticker in tickers)
        {
            // Quarterly contracts look like BTCUSDT_250926, so they don't end in the quote
            // asset and are skipped here without needing a special case.
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

        return matches;
    }

    /// <summary>
    /// Second pass: tags each candidate as crypto or tokenized stock from its contract type
    /// and keeps only the ones clearing the threshold for their own kind, sorted from
    /// highest to lowest gain. Symbols missing from the metadata, or not in TRADING status
    /// when <paramref name="onlyTradingSymbols"/> is set, are reported through
    /// <paramref name="discarded"/> so the caller can log why.
    /// </summary>
    public static IReadOnlyList<Coin> Classify(
        IEnumerable<Coin> candidates,
        IReadOnlyDictionary<string, SymbolInfo> metadata,
        decimal minChangePercent,
        decimal stockMinChangePercent,
        bool onlyTradingSymbols,
        out IReadOnlyList<(Coin Coin, string Reason)> discarded)
    {
        var kept = new List<Coin>();
        var dropped = new List<(Coin, string)>();

        foreach (var coin in candidates)
        {
            if (!metadata.TryGetValue(coin.Symbol, out var info))
            {
                dropped.Add((coin, "not present in exchangeInfo"));
                continue;
            }

            if (onlyTradingSymbols && !string.Equals(info.Status, "TRADING", StringComparison.Ordinal))
            {
                dropped.Add((coin, $"status is {info.Status}, not TRADING"));
                continue;
            }

            var kind = string.Equals(info.ContractType, TradFiContractType, StringComparison.Ordinal)
                ? CoinKind.TokenizedStock
                : CoinKind.Crypto;

            var threshold = kind == CoinKind.TokenizedStock ? stockMinChangePercent : minChangePercent;
            if (coin.ChangePercent < threshold)
                continue;

            kept.Add(coin with { Kind = kind });
        }

        discarded = dropped;
        return kept.OrderByDescending(c => c.ChangePercent).ToList();
    }

    /// <summary>Counts how many symbols on the exchange belong to the given quote asset.</summary>
    public static int CountQuotePairs(IEnumerable<Ticker24h> tickers, string quoteAsset) =>
        tickers.Count(t =>
            t.Symbol.Length > quoteAsset.Length &&
            t.Symbol.EndsWith(quoteAsset, StringComparison.Ordinal));

    /// <summary>
    /// Binance sends every numeric field as a string. This is the one place that turns them
    /// into numbers, always with the invariant culture — a machine set to a comma decimal
    /// separator would otherwise read "1.5" as 15.
    /// </summary>
    internal static bool TryParse(string value, out decimal result) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    /// <summary>Same, for fields where an unreadable value is better treated as zero than as an error.</summary>
    internal static decimal Parse(string value) => TryParse(value, out var result) ? result : 0m;
}
