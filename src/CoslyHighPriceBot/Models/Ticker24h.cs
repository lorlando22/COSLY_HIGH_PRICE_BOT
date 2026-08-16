using System.Text.Json.Serialization;

namespace CoslyHighPriceBot.Models;

/// <summary>
/// Respuesta de /api/v3/ticker/24hr. Binance devuelve todos los valores numéricos
/// como string, así que el parseo a decimal se hace después (ver <see cref="Coin"/>).
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

/// <summary>Respuesta de /api/v3/ticker (ventana móvil); sólo nos interesa la variación.</summary>
internal sealed class WindowTicker
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = "";

    [JsonPropertyName("priceChangePercent")]
    public string PriceChangePercent { get; set; } = "";
}

/// <summary>Respuesta de /api/v3/exchangeInfo, recortada a lo que usamos.</summary>
internal sealed class ExchangeInfo
{
    [JsonPropertyName("symbols")]
    public List<SymbolInfo> Symbols { get; set; } = [];
}

internal sealed class SymbolInfo
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = "";

    /// <summary>TRADING, BREAK, HALT... Sólo TRADING se puede operar.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
}

/// <summary>Variación de una moneda en una ventana corta (por ejemplo "4h").</summary>
internal sealed record WindowChange(string Window, decimal ChangePercent);

/// <summary>Moneda ya parseada y lista para mostrar.</summary>
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
    /// <summary>Variaciones en ventanas cortas, en el orden configurado en Binance:ExtraWindows.</summary>
    public IReadOnlyList<WindowChange> WindowChanges { get; init; } = [];
}
