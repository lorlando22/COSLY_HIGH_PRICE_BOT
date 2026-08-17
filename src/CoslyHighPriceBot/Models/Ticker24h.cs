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

/// <summary>Response from /api/v3/ticker (rolling window); we only care about the change percent.</summary>
internal sealed class WindowTicker
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = "";

    [JsonPropertyName("priceChangePercent")]
    public string PriceChangePercent { get; set; } = "";
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

/// <summary>A coin's change percent over a short window (e.g. "4h").</summary>
internal sealed record WindowChange(string Window, decimal ChangePercent);

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
    /// <summary>Short-window changes, in the order configured in Binance:ExtraWindows.</summary>
    public IReadOnlyList<WindowChange> WindowChanges { get; init; } = [];
}
