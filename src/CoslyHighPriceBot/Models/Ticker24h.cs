using System.Text.Json.Serialization;

namespace CoslyHighPriceBot.Models;

/// <summary>
/// Response from /fapi/v1/ticker/24hr. Binance returns every numeric value as a string,
/// so parsing to decimal happens later (see <see cref="Coin"/>).
/// </summary>
internal sealed class Ticker24h
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
internal sealed class ExchangeInfo
{
    [JsonPropertyName("symbols")]
    public List<SymbolInfo> Symbols { get; set; } = [];
}

internal sealed class SymbolInfo
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
    /// Quarterly contracts use CURRENT_QUARTER / NEXT_QUARTER.
    /// </summary>
    [JsonPropertyName("contractType")]
    public string ContractType { get; set; } = "";
}

/// <summary>
/// What kind of asset a symbol represents. Each kind has its own threshold, its own
/// Telegram message and its own already-notified file.
/// </summary>
internal enum CoinKind
{
    Crypto,
    TokenizedStock
}

/// <summary>A coin already parsed and ready to display.</summary>
internal sealed record Coin(
    string Symbol,
    string QuoteAsset,
    decimal ChangePercent,
    decimal LastPrice,
    decimal OpenPrice,
    decimal HighPrice,
    decimal LowPrice,
    decimal QuoteVolume,
    long TradeCount)
{
    /// <summary>Set once the symbol's contract type is known (see <see cref="SymbolInfo"/>).</summary>
    public CoinKind Kind { get; init; } = CoinKind.Crypto;
}
