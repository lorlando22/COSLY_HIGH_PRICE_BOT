using System.Text.Json.Serialization;

namespace CoslyHighPriceBot.Models;

/// <summary>
/// Response from /api/v3/ticker/24hr. Binance returns every numeric value as a string,
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

/// <summary>Response from /api/v3/exchangeInfo, trimmed down to what we use.</summary>
internal sealed class ExchangeInfo
{
    [JsonPropertyName("symbols")]
    public List<SymbolInfo> Symbols { get; set; } = [];
}

internal sealed class SymbolInfo
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = "";

    /// <summary>TRADING, BREAK, HALT... Only TRADING can actually be traded.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
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
    string BaseAsset,
    string QuoteAsset,
    CoinKind Kind,
    decimal ChangePercent,
    decimal LastPrice,
    decimal OpenPrice,
    decimal HighPrice,
    decimal LowPrice,
    decimal QuoteVolume,
    long TradeCount);
