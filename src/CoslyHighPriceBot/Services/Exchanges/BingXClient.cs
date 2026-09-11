using System.Text.Json.Serialization;
using CoslyHighPriceBot.Configuration;
using CoslyHighPriceBot.Models;

namespace CoslyHighPriceBot.Services.Exchanges;

/// <summary>Reads prices and metadata from BingX's public USDT-margined perpetuals API.</summary>
internal sealed class BingXClient(ExchangeHttp http, BingXOptions options) : IExchangeClient
{
    private const int OnlineStatus = 1;
    private const string Currency = "USDT";

    /// <summary>Symbol prefixes BingX uses for its tokenized TradFi instruments (stocks, indices/ETFs, commodities, forex).</summary>
    private static readonly string[] TradFiPrefixes = ["NCSK", "NCSI", "NCCO", "NCFX"];

    public string Name => "BingX";

    public async Task<IReadOnlyList<MarketTicker>> GetTickersAsync(CancellationToken cancellationToken)
    {
        var response = await http.GetJsonAsync<TickerEnvelope>(options.TickerUrl, cancellationToken);
        if (response is null)
            throw new InvalidOperationException("BingX returned an empty response.");

        if (response.Code != 0)
            throw new InvalidOperationException($"BingX ticker error {response.Code}: {response.Msg}");

        var result = new List<MarketTicker>(response.Data.Count);
        foreach (var ticker in response.Data)
        {
            // Unlike Bybit, BingX already reports this in percentage points.
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
                TradeCount: null)); // BingX's ticker doesn't report a trade count.
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<string, InstrumentInfo>> GetInstrumentsAsync(CancellationToken cancellationToken)
    {
        var response = await http.GetJsonAsync<ContractsEnvelope>(options.ContractsUrl, cancellationToken);
        if (response is null)
            throw new InvalidOperationException("BingX returned an empty response.");

        if (response.Code != 0)
            throw new InvalidOperationException($"BingX contracts error {response.Code}: {response.Msg}");

        return response.Data.ToDictionary(c => c.Symbol, Classify, StringComparer.Ordinal);
    }

    private static InstrumentInfo Classify(Contract contract)
    {
        if (!string.Equals(contract.Currency, Currency, StringComparison.Ordinal))
            return Discard($"settled in {contract.Currency}, not {Currency}"); // the USDC-settled twin of a USDT symbol.

        // apiStateOpen is deliberately never used here: it only says whether a position can
        // be opened through the API, and several contracts with real volume (POWER, MAGMA,
        // SPORTFUN) trade normally from the app with apiStateOpen=false. status is the field
        // that actually reflects whether the contract is listed and open (1 = ONLINE).
        if (contract.Status != OnlineStatus)
            return Discard($"status is {contract.Status}, not {OnlineStatus} (ONLINE)");

        var symbolBase = SymbolBase(contract.Symbol);

        var info = IsTradFiSymbol(symbolBase)
            ? new InstrumentInfo(true, CoinKind.TokenizedStock, TradFiAliases(contract, symbolBase), 1m, null)
            : Crypto(contract, symbolBase);

        // The app lists NCSKTSLA2USD-USDT as "TSLA-USDT" and MONAD-USDT as "MON-USDT": the
        // message has to show what a trader will actually find when searching BingX.
        return string.IsNullOrWhiteSpace(contract.DisplayName) || string.Equals(contract.DisplayName, contract.Symbol, StringComparison.Ordinal)
            ? info
            : info with { DisplaySymbol = contract.DisplayName };
    }

    private static InstrumentInfo Crypto(Contract contract, string symbolBase)
    {
        var (symbolName, multiplier) = SymbolNormalizer.Normalize(symbolBase);
        var (displayBase, _) = SplitDisplayName(contract.DisplayName);
        var (displayName, _) = SymbolNormalizer.Normalize(displayBase);

        // BingX uses an internal symbol that doesn't match the coin's real ticker for close
        // to 30 coins (MONAD -> MON, LIGHTER -> LIT, NEIROCTO -> NEIRO...); displayName is the
        // one that lines up with Binance and Bybit, so both are kept as aliases when they differ.
        var aliases = new List<string> { symbolName };
        if (!string.Equals(displayName, symbolName, StringComparison.Ordinal))
            aliases.Add(displayName);

        return new InstrumentInfo(true, CoinKind.Crypto, aliases, multiplier, null);
    }

    private static IReadOnlyList<string> TradFiAliases(Contract contract, string symbolBase)
    {
        var aliases = new List<string> { ExtractTradFiTicker(symbolBase) };

        var (displayBase, parenthetical) = SplitDisplayName(contract.DisplayName);
        if (!aliases.Contains(displayBase, StringComparer.Ordinal))
            aliases.Add(displayBase);

        // Only NCCO (commodities) carries one: "GOLD(XAU)" -> alias "XAU" on top of "GOLD",
        // which is what lets it line up with Bybit's plain XAUUSDT (baseCoin "XAU").
        if (parenthetical is not null && !aliases.Contains(parenthetical, StringComparer.Ordinal))
            aliases.Add(parenthetical);

        return aliases;
    }

    private static bool IsTradFiSymbol(string symbolBase) =>
        TradFiPrefixes.Any(prefix => symbolBase.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>The symbol without its "-USDT"/"-USDC" quote suffix, e.g. "1000PEPE-USDT" -> "1000PEPE".</summary>
    private static string SymbolBase(string symbol)
    {
        var dash = symbol.IndexOf('-');
        return dash > 0 ? symbol[..dash] : symbol;
    }

    /// <summary>
    /// NCSKTSLA2USD -> TSLA, NCCOGOLD2USD -> GOLD: drop the 4-letter category prefix and the
    /// "2USD" marker onward. A few contracts skip the marker and end in the quote instead
    /// (NCSKSITMUSDT -> SITM), so a trailing USDT/USD is trimmed when there's no marker.
    /// </summary>
    private static string ExtractTradFiTicker(string symbolBase)
    {
        var withoutPrefix = symbolBase[4..];
        var marker = withoutPrefix.IndexOf("2USD", StringComparison.Ordinal);
        if (marker > 0)
            return withoutPrefix[..marker];

        foreach (var quote in (string[])["USDT", "USD"])
        {
            if (withoutPrefix.Length > quote.Length && withoutPrefix.EndsWith(quote, StringComparison.Ordinal))
                return withoutPrefix[..^quote.Length];
        }

        return withoutPrefix;
    }

    /// <summary>"GOLD(XAU)-USDT" -> ("GOLD", "XAU"); "MEME-USDT" -> ("MEME", null).</summary>
    private static (string Base, string? Parenthetical) SplitDisplayName(string displayName)
    {
        var dash = displayName.LastIndexOf('-');
        var namePart = dash > 0 ? displayName[..dash] : displayName;

        var open = namePart.IndexOf('(');
        if (open < 0)
            return (namePart, null);

        var close = namePart.IndexOf(')', open);
        var inner = close > open ? namePart[(open + 1)..close] : null;
        return (namePart[..open], inner);
    }

    private static InstrumentInfo Discard(string reason) => new(false, null, [], 1m, reason);

    private sealed class TickerEnvelope
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("msg")]
        public string Msg { get; set; } = "";

        [JsonPropertyName("data")]
        public List<Ticker> Data { get; set; } = [];
    }

    private sealed class Ticker
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
    }

    private sealed class ContractsEnvelope
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("msg")]
        public string Msg { get; set; } = "";

        [JsonPropertyName("data")]
        public List<Contract> Data { get; set; } = [];
    }

    private sealed class Contract
    {
        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = "";

        [JsonPropertyName("currency")]
        public string Currency { get; set; } = "";

        /// <summary>0 OFFLINE, 1 ONLINE, 5 PRE_ONLINE, 25 FORBIDDEN_TO_OPEN. A JSON number, unlike apiStateOpen which is a string.</summary>
        [JsonPropertyName("status")]
        public int Status { get; set; }

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = "";
    }
}
