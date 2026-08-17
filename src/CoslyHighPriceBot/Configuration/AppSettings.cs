using System.Text.RegularExpressions;

namespace CoslyHighPriceBot.Configuration;

/// <summary>Mirrors appsettings.json. Populated via IConfiguration.Get&lt;AppSettings&gt;().</summary>
internal sealed class AppSettings
{
    public BinanceOptions Binance { get; set; } = new();
    public FilterOptions Filter { get; set; } = new();
    public TelegramOptions Telegram { get; set; } = new();
    public StateOptions State { get; set; } = new();
    public LoggingOptions Logging { get; set; } = new();

    /// <summary>Returns the list of configuration problems. Empty = everything is fine.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!Uri.TryCreate(Binance.Ticker24hUrl, UriKind.Absolute, out _))
            errors.Add("Binance:Ticker24hUrl must be an absolute URL.");

        if (!Uri.TryCreate(Binance.RollingTickerUrl, UriKind.Absolute, out _))
            errors.Add("Binance:RollingTickerUrl must be an absolute URL.");

        if (!Uri.TryCreate(Binance.ExchangeInfoUrl, UriKind.Absolute, out _))
            errors.Add("Binance:ExchangeInfoUrl must be an absolute URL.");

        if (string.IsNullOrWhiteSpace(Binance.QuoteAsset))
            errors.Add("Binance:QuoteAsset cannot be empty (e.g.: USDT).");

        foreach (var window in Binance.ExtraWindows)
        {
            // Binance accepts 1m-59m, 1h-23h and 1d-7d.
            if (!Regex.IsMatch(window, "^[1-9][0-9]?[mhd]$"))
                errors.Add($"Binance:ExtraWindows contains an invalid value: '{window}' (expected something like 1h, 4h, 30m or 3d).");
        }

        if (Filter.MinChangePercent <= 0)
            errors.Add("Filter:MinChangePercent must be greater than 0.");

        if (!Uri.TryCreate(Telegram.ApiBaseUrl, UriKind.Absolute, out _))
            errors.Add("Telegram:ApiBaseUrl must be an absolute URL.");

        if (string.IsNullOrWhiteSpace(Telegram.BotToken))
            errors.Add("Telegram:BotToken cannot be empty.");

        if (string.IsNullOrWhiteSpace(Telegram.ChatId))
            errors.Add("Telegram:ChatId cannot be empty.");

        if (string.IsNullOrWhiteSpace(State.NotifiedSymbolsFile))
            errors.Add("State:NotifiedSymbolsFile cannot be empty.");

        if (Logging.RetentionDays < 0)
            errors.Add("Logging:RetentionDays cannot be negative (0 = keep every log).");

        return errors;
    }
}

internal sealed class BinanceOptions
{
    /// <summary>24-hour ticker. With no query string it returns every symbol.</summary>
    public string Ticker24hUrl { get; set; } = "https://api.binance.com/api/v3/ticker/24hr";

    /// <summary>Rolling-window ticker; used for the 4h and 1h changes.</summary>
    public string RollingTickerUrl { get; set; } = "https://api.binance.com/api/v3/ticker";

    /// <summary>Exchange info; used to know which symbols are tradable.</summary>
    public string ExchangeInfoUrl { get; set; } = "https://api.binance.com/api/v3/exchangeInfo";

    /// <summary>Quote asset to consider; symbols ending in it are the ones kept.</summary>
    public string QuoteAsset { get; set; } = "USDT";

    /// <summary>
    /// Short windows to show in addition to the 24h one, in the order they appear in the
    /// message. Starts empty on purpose: the configuration binder calls Add() on the
    /// existing list, so any default value here would end up duplicated with the one in
    /// appsettings.json.
    /// </summary>
    public List<string> ExtraWindows { get; set; } = [];

    /// <summary>
    /// Discards symbols that aren't in TRADING status. Suspended pairs (BREAK/HALT) keep
    /// their 24h stats frozen and generate pump alerts for coins that can't actually be traded.
    /// </summary>
    public bool OnlyTradingSymbols { get; set; } = true;
}

internal sealed class FilterOptions
{
    /// <summary>Minimum 24h gain (in %) for a coin to make it into the alert.</summary>
    public decimal MinChangePercent { get; set; } = 100m;
}

internal sealed class StateOptions
{
    /// <summary>
    /// File where already-notified symbols are remembered. If it's a relative path,
    /// it's resolved against the executable's folder, not the working directory.
    /// </summary>
    public string NotifiedSymbolsFile { get; set; } = "notified-symbols.json";
}

internal sealed class LoggingOptions
{
    /// <summary>Days of logs kept in the Logs folder. 0 means none get deleted.</summary>
    public int RetentionDays { get; set; } = 30;
}

internal sealed class TelegramOptions
{
    public string ApiBaseUrl { get; set; } = "https://api.telegram.org";
    public string BotToken { get; set; } = "";
    public string ChatId { get; set; } = "";
}
