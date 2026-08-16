using System.Text.RegularExpressions;

namespace CoslyHighPriceBot.Configuration;

/// <summary>Espejo de appsettings.json. Se completa con IConfiguration.Get&lt;AppSettings&gt;().</summary>
internal sealed class AppSettings
{
    public BinanceOptions Binance { get; set; } = new();
    public FilterOptions Filter { get; set; } = new();
    public TelegramOptions Telegram { get; set; } = new();
    public StateOptions State { get; set; } = new();

    /// <summary>Devuelve la lista de problemas de configuración. Vacía = todo en orden.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!Uri.TryCreate(Binance.Ticker24hUrl, UriKind.Absolute, out _))
            errors.Add("Binance:Ticker24hUrl debe ser una URL absoluta.");

        if (!Uri.TryCreate(Binance.RollingTickerUrl, UriKind.Absolute, out _))
            errors.Add("Binance:RollingTickerUrl debe ser una URL absoluta.");

        if (!Uri.TryCreate(Binance.ExchangeInfoUrl, UriKind.Absolute, out _))
            errors.Add("Binance:ExchangeInfoUrl debe ser una URL absoluta.");

        if (string.IsNullOrWhiteSpace(Binance.QuoteAsset))
            errors.Add("Binance:QuoteAsset no puede estar vacío (por ejemplo: USDT).");

        foreach (var window in Binance.ExtraWindows)
        {
            // Binance acepta 1m-59m, 1h-23h y 1d-7d.
            if (!Regex.IsMatch(window, "^[1-9][0-9]?[mhd]$"))
                errors.Add($"Binance:ExtraWindows contiene un valor inválido: '{window}' (se espera algo como 1h, 4h, 30m o 3d).");
        }

        if (Filter.MinChangePercent <= 0)
            errors.Add("Filter:MinChangePercent debe ser mayor que 0.");

        if (!Uri.TryCreate(Telegram.ApiBaseUrl, UriKind.Absolute, out _))
            errors.Add("Telegram:ApiBaseUrl debe ser una URL absoluta.");

        if (string.IsNullOrWhiteSpace(Telegram.BotToken))
            errors.Add("Telegram:BotToken no puede estar vacío.");

        if (string.IsNullOrWhiteSpace(Telegram.ChatId))
            errors.Add("Telegram:ChatId no puede estar vacío.");

        if (string.IsNullOrWhiteSpace(State.NotifiedSymbolsFile))
            errors.Add("State:NotifiedSymbolsFile no puede estar vacío.");

        return errors;
    }
}

internal sealed class BinanceOptions
{
    /// <summary>Ticker de 24 horas. Sin query string devuelve todos los símbolos.</summary>
    public string Ticker24hUrl { get; set; } = "https://api.binance.com/api/v3/ticker/24hr";

    /// <summary>Ticker de ventana móvil; se usa para las variaciones de 4h y 1h.</summary>
    public string RollingTickerUrl { get; set; } = "https://api.binance.com/api/v3/ticker";

    /// <summary>Información del exchange; se usa para saber qué símbolos están operables.</summary>
    public string ExchangeInfoUrl { get; set; } = "https://api.binance.com/api/v3/exchangeInfo";

    /// <summary>Moneda de cotización a considerar; se filtran los símbolos que terminan así.</summary>
    public string QuoteAsset { get; set; } = "USDT";

    /// <summary>
    /// Ventanas cortas a mostrar además de las 24h, en el orden en que aparecen en el mensaje.
    /// Arranca vacía a propósito: el binder de configuración hace Add() sobre la lista existente,
    /// así que cualquier valor por defecto acá terminaría duplicado con el de appsettings.json.
    /// </summary>
    public List<string> ExtraWindows { get; set; } = [];

    /// <summary>
    /// Descarta los símbolos que no están en estado TRADING. Los pares suspendidos (BREAK/HALT)
    /// conservan sus estadísticas de 24h congeladas y generan avisos de pumps que no se pueden operar.
    /// </summary>
    public bool OnlyTradingSymbols { get; set; } = true;
}

internal sealed class FilterOptions
{
    /// <summary>Suba mínima en 24h (en %) para que una moneda entre en el aviso.</summary>
    public decimal MinChangePercent { get; set; } = 100m;
}

internal sealed class StateOptions
{
    /// <summary>
    /// Archivo donde se recuerdan los símbolos ya avisados. Si es una ruta relativa,
    /// se resuelve contra la carpeta del ejecutable, no contra el directorio de trabajo.
    /// </summary>
    public string NotifiedSymbolsFile { get; set; } = "notified-symbols.json";
}

internal sealed class TelegramOptions
{
    public string ApiBaseUrl { get; set; } = "https://api.telegram.org";
    public string BotToken { get; set; } = "";
    public string ChatId { get; set; } = "";
}
