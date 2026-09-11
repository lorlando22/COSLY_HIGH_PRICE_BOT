using System.Text.Json.Serialization;
using CoslyHighPriceBot.Configuration;
using CoslyHighPriceBot.Models;

namespace CoslyHighPriceBot.Services.Exchanges;

/// <summary>Reads prices and metadata from Bybit's public linear (USDT/USDC-margined) perpetuals API.</summary>
internal sealed class BybitClient(ExchangeHttp http, BybitOptions options) : IExchangeClient
{
    private const string LinearPerpetual = "LinearPerpetual";
    private const string TradingStatus = "Trading";
    private const string SettleCoin = "USDT";
    private const string StockSuffix = "STOCK";

    /// <summary>
    /// symbolType values Bybit uses for its tokenized TradFi instruments. "innovation" is a
    /// risk tier for newer crypto, not a TradFi marker, so it's grouped with the empty string
    /// (an ordinary coin) in <see cref="KnownCryptoTiers"/> instead.
    /// </summary>
    private static readonly HashSet<string> KnownStockTiers = new(StringComparer.Ordinal) { "stock", "ETF", "commodity", "forex" };

    private static readonly HashSet<string> KnownCryptoTiers = new(StringComparer.Ordinal) { "", "innovation" };

    public string Name => "Bybit";

    public async Task<IReadOnlyList<MarketTicker>> GetTickersAsync(CancellationToken cancellationToken)
    {
        var response = await http.GetJsonAsync<TickersResponse>(options.TickersUrl, cancellationToken);
        if (response is null)
            throw new InvalidOperationException("Bybit returned an empty response.");

        if (response.RetCode != 0)
            throw new InvalidOperationException($"Bybit ticker error {response.RetCode}: {response.RetMsg}");

        var result = new List<MarketTicker>(response.Result.List.Count);
        foreach (var ticker in response.Result.List)
        {
            // Bybit sends the 24h change as a fraction (0.047644 = +4.76%); the rest of the
            // bot works in percentage points, like every other exchange.
            if (!CoinFilter.TryParse(ticker.Price24hPcnt, out var fraction))
                continue;

            result.Add(new MarketTicker(
                Symbol: ticker.Symbol,
                ChangePercent: fraction * 100m,
                LastPrice: CoinFilter.Parse(ticker.LastPrice),
                OpenPrice: CoinFilter.Parse(ticker.PrevPrice24h),
                HighPrice: CoinFilter.Parse(ticker.HighPrice24h),
                LowPrice: CoinFilter.Parse(ticker.LowPrice24h),
                QuoteVolume: CoinFilter.Parse(ticker.Turnover24h),
                TradeCount: null)); // Bybit's ticker doesn't report a trade count.
        }

        return result;
    }

    /// <summary>
    /// Paginates through the full instrument catalog: the endpoint defaults to 500 per page
    /// and Bybit currently lists more contracts than that, so a single request isn't enough.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, InstrumentInfo>> GetInstrumentsAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, InstrumentInfo>(StringComparer.Ordinal);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;

        do
        {
            var url = cursor is null ? options.InstrumentsUrl : $"{options.InstrumentsUrl}&cursor={Uri.EscapeDataString(cursor)}";
            var response = await http.GetJsonAsync<InstrumentsResponse>(url, cancellationToken);
            if (response is null)
                throw new InvalidOperationException("Bybit returned an empty response.");

            if (response.RetCode != 0)
                throw new InvalidOperationException($"Bybit instruments error {response.RetCode}: {response.RetMsg}");

            foreach (var instrument in response.Result.List)
                result[instrument.Symbol] = Classify(instrument);

            cursor = string.IsNullOrEmpty(response.Result.NextPageCursor) ? null : response.Result.NextPageCursor;

            // A cursor handed back twice would page forever; fail the fetch instead.
            if (cursor is not null && !seenCursors.Add(cursor))
                throw new InvalidOperationException("Bybit returned the same instruments page cursor twice.");
        } while (cursor is not null);

        return result;
    }

    private static InstrumentInfo Classify(Instrument instrument)
    {
        if (!string.Equals(instrument.ContractType, LinearPerpetual, StringComparison.Ordinal))
            return Discard($"contract type is {instrument.ContractType}, not {LinearPerpetual}"); // dated futures (BTCUSDT-25SEP26) end up here.

        // The endpoint also returns PendingOpen by default alongside Trading, so this check
        // has to be explicit rather than assumed.
        if (!string.Equals(instrument.Status, TradingStatus, StringComparison.Ordinal))
            return Discard($"status is {instrument.Status}, not {TradingStatus}");

        if (!string.Equals(instrument.SettleCoin, SettleCoin, StringComparison.Ordinal))
            return Discard($"settled in {instrument.SettleCoin}, not {SettleCoin}"); // the USDC-settled twin of a USDT symbol.

        if (instrument.IsPreListing)
            return Discard("still pre-listing");

        if (KnownStockTiers.Contains(instrument.SymbolType))
            return new InstrumentInfo(true, CoinKind.TokenizedStock, StockAliases(instrument), 1m, null);

        if (KnownCryptoTiers.Contains(instrument.SymbolType))
        {
            var (name, multiplier) = SymbolNormalizer.Normalize(instrument.BaseCoin);
            return new InstrumentInfo(true, CoinKind.Crypto, [name], multiplier, null);
        }

        // A tier Bybit hasn't documented yet: guessing crypto or stock risks flooding the
        // wrong message (a new crypto tier sent at the much lower stock threshold, or vice
        // versa), so it's discarded and logged instead of guessed at.
        return Discard($"unknown symbolType '{instrument.SymbolType}'");
    }

    private static IReadOnlyList<string> StockAliases(Instrument instrument)
    {
        var baseName = instrument.BaseCoin.EndsWith(StockSuffix, StringComparison.Ordinal)
            ? instrument.BaseCoin[..^StockSuffix.Length]
            : instrument.BaseCoin;

        var aliases = new List<string> { baseName };

        // underlyingTicker is the real market ticker (AMD, CAT...) and usually matches the
        // trimmed baseCoin already; it's only added when it's letters-only and different, to
        // avoid pulling in something like an exchange code or a numeric identifier.
        if (!string.IsNullOrEmpty(instrument.UnderlyingTicker) &&
            instrument.UnderlyingTicker.All(char.IsAsciiLetterUpper) &&
            !string.Equals(instrument.UnderlyingTicker, baseName, StringComparison.Ordinal))
            aliases.Add(instrument.UnderlyingTicker);

        return aliases;
    }

    private static InstrumentInfo Discard(string reason) => new(false, null, [], 1m, reason);

    private sealed class TickersResponse
    {
        [JsonPropertyName("retCode")]
        public int RetCode { get; set; }

        [JsonPropertyName("retMsg")]
        public string RetMsg { get; set; } = "";

        [JsonPropertyName("result")]
        public TickersResult Result { get; set; } = new();
    }

    private sealed class TickersResult
    {
        [JsonPropertyName("list")]
        public List<Ticker> List { get; set; } = [];
    }

    private sealed class Ticker
    {
        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = "";

        [JsonPropertyName("lastPrice")]
        public string LastPrice { get; set; } = "";

        [JsonPropertyName("prevPrice24h")]
        public string PrevPrice24h { get; set; } = "";

        [JsonPropertyName("price24hPcnt")]
        public string Price24hPcnt { get; set; } = "";

        [JsonPropertyName("highPrice24h")]
        public string HighPrice24h { get; set; } = "";

        [JsonPropertyName("lowPrice24h")]
        public string LowPrice24h { get; set; } = "";

        [JsonPropertyName("turnover24h")]
        public string Turnover24h { get; set; } = "";
    }

    private sealed class InstrumentsResponse
    {
        [JsonPropertyName("retCode")]
        public int RetCode { get; set; }

        [JsonPropertyName("retMsg")]
        public string RetMsg { get; set; } = "";

        [JsonPropertyName("result")]
        public InstrumentsResult Result { get; set; } = new();
    }

    private sealed class InstrumentsResult
    {
        [JsonPropertyName("list")]
        public List<Instrument> List { get; set; } = [];

        [JsonPropertyName("nextPageCursor")]
        public string NextPageCursor { get; set; } = "";
    }

    private sealed class Instrument
    {
        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = "";

        [JsonPropertyName("contractType")]
        public string ContractType { get; set; } = "";

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("baseCoin")]
        public string BaseCoin { get; set; } = "";

        [JsonPropertyName("settleCoin")]
        public string SettleCoin { get; set; } = "";

        [JsonPropertyName("isPreListing")]
        public bool IsPreListing { get; set; }

        /// <summary>
        /// "" (ordinary coin) or "innovation" for crypto; "stock", "ETF", "commodity" or
        /// "forex" for a tokenized TradFi instrument. Any other value is unrecognized and
        /// discarded rather than guessed at (see Classify).
        /// </summary>
        [JsonPropertyName("symbolType")]
        public string SymbolType { get; set; } = "";

        [JsonPropertyName("underlyingTicker")]
        public string UnderlyingTicker { get; set; } = "";
    }
}
