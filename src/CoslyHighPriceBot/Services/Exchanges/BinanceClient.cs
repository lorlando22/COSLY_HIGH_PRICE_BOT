using System.Text.Json.Serialization;
using CoslyHighPriceBot.Configuration;
using CoslyHighPriceBot.Models;

namespace CoslyHighPriceBot.Services.Exchanges;

/// <summary>Reads prices and metadata from Binance's public USD-M futures API. Behaviour is unchanged from the single-exchange bot.</summary>
internal sealed class BinanceClient(ExchangeHttp http, BinanceOptions options) : IExchangeClient
{
    /// <summary>Contract type Binance uses for tokenized equities, commodities and other TradFi instruments.</summary>
    private const string TradFiContractType = "TRADIFI_PERPETUAL";
    private const string CryptoContractType = "PERPETUAL";
    private const string TradingStatus = "TRADING";

    public string Name => "Binance";

    /// <summary>
    /// Fetches the 24h ticker for every futures symbol (about 750-900, a few hundred KB).
    /// It's a single call per scan, so there's no point paginating or filtering server-side.
    /// </summary>
    public async Task<IReadOnlyList<MarketTicker>> GetTickersAsync(CancellationToken cancellationToken)
    {
        var tickers = await http.GetJsonAsync<List<Ticker24h>>(options.Ticker24hUrl, cancellationToken);
        if (tickers is null)
            throw new InvalidOperationException("Binance returned an empty response.");

        var result = new List<MarketTicker>(tickers.Count);
        foreach (var ticker in tickers)
        {
            if (!CoinFilter.TryParse(ticker.PriceChangePercent, out var changePercent))
                continue;

            result.Add(new MarketTicker(
                Symbol: ticker.Symbol,
                ChangePercent: changePercent,
                LastPrice: CoinFilter.Parse(ticker.LastPrice),
                OpenPrice: CoinFilter.Parse(ticker.OpenPrice),
                HighPrice: CoinFilter.Parse(ticker.HighPrice),
                LowPrice: CoinFilter.Parse(ticker.LowPrice),
                QuoteVolume: CoinFilter.Parse(ticker.QuoteVolume),
                TradeCount: ticker.TradeCount));
        }

        return result;
    }

    /// <summary>
    /// Status, contract type and quote asset for every symbol. Unlike the ticker, futures
    /// exchangeInfo takes no `symbols` filter: it always returns the full catalog, which is
    /// why callers go through <see cref="InstrumentCache"/> instead of calling this every scan.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, InstrumentInfo>> GetInstrumentsAsync(CancellationToken cancellationToken)
    {
        var info = await http.GetJsonAsync<ExchangeInfo>(options.ExchangeInfoUrl, cancellationToken);

        return (info?.Symbols ?? [])
            .GroupBy(s => s.Symbol, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Classify(g.First()), StringComparer.Ordinal);
    }

    private InstrumentInfo Classify(SymbolInfo info)
    {
        if (options.OnlyTradingSymbols && !string.Equals(info.Status, TradingStatus, StringComparison.Ordinal))
            return Discard($"status is {info.Status}, not {TradingStatus}");

        if (!string.Equals(info.QuoteAsset, options.QuoteAsset, StringComparison.Ordinal))
            return Discard($"quote asset is {info.QuoteAsset}, not {options.QuoteAsset}");

        return info.ContractType switch
        {
            CryptoContractType => Crypto(info.BaseAsset),
            TradFiContractType => new InstrumentInfo(true, CoinKind.TokenizedStock, [info.BaseAsset], 1m, null),
            // A dated quarterly contract (CURRENT_QUARTER / NEXT_QUARTER) or a future contract
            // type Binance adds later: neither is a perpetual, so it's discarded rather than
            // guessed at.
            _ => Discard($"unsupported contract type {info.ContractType}")
        };
    }

    private static InstrumentInfo Crypto(string baseAsset)
    {
        var (name, multiplier) = SymbolNormalizer.Normalize(baseAsset);
        return new InstrumentInfo(true, CoinKind.Crypto, [name], multiplier, null);
    }

    private static InstrumentInfo Discard(string reason) => new(false, null, [], 1m, reason);

    /// <summary>
    /// Response from /fapi/v1/ticker/24hr. Binance returns every numeric value as a string,
    /// so parsing to decimal happens in <see cref="GetTickersAsync"/>.
    /// </summary>
    private sealed class Ticker24h
    {
        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = "";

        [JsonPropertyName("priceChangePercent")]
        public string PriceChangePercent { get; set; } = "";

        [JsonPropertyName("lastPrice")]
        public string LastPrice { get; set; } = "";

        [JsonPropertyName("openPrice")]
        public string OpenPrice { get; set; } = "";

        [JsonPropertyName("highPrice")]
        public string HighPrice { get; set; } = "";

        [JsonPropertyName("lowPrice")]
        public string LowPrice { get; set; } = "";

        [JsonPropertyName("quoteVolume")]
        public string QuoteVolume { get; set; } = "";

        [JsonPropertyName("count")]
        public long TradeCount { get; set; }
    }

    /// <summary>Response from /fapi/v1/exchangeInfo, trimmed down to what we use.</summary>
    private sealed class ExchangeInfo
    {
        [JsonPropertyName("symbols")]
        public List<SymbolInfo> Symbols { get; set; } = [];
    }

    private sealed class SymbolInfo
    {
        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = "";

        /// <summary>TRADING, SETTLING, PENDING_TRADING... Only TRADING can actually be traded.</summary>
        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        /// <summary>
        /// PERPETUAL for crypto, TRADIFI_PERPETUAL for tokenized equities, commodities and
        /// other traditional-finance instruments. This is the field that classifies a symbol,
        /// and the reason the bot reads futures instead of spot: spot has no equivalent.
        /// Quarterly contracts use CURRENT_QUARTER / NEXT_QUARTER and are discarded.
        /// </summary>
        [JsonPropertyName("contractType")]
        public string ContractType { get; set; } = "";

        [JsonPropertyName("baseAsset")]
        public string BaseAsset { get; set; } = "";

        [JsonPropertyName("quoteAsset")]
        public string QuoteAsset { get; set; } = "";
    }
}
