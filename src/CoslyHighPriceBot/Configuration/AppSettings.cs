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

        if (!Uri.TryCreate(Binance.ExchangeInfoUrl, UriKind.Absolute, out _))
            errors.Add("Binance:ExchangeInfoUrl must be an absolute URL.");

        if (string.IsNullOrWhiteSpace(Binance.QuoteAsset))
            errors.Add("Binance:QuoteAsset cannot be empty (e.g.: USDT).");

        if (Filter.MinChangePercent <= 0)
            errors.Add("Filter:MinChangePercent must be greater than 0.");

        if (Filter.StockMinChangePercent <= 0)
            errors.Add("Filter:StockMinChangePercent must be greater than 0.");

        if (!Uri.TryCreate(Telegram.ApiBaseUrl, UriKind.Absolute, out _))
            errors.Add("Telegram:ApiBaseUrl must be an absolute URL.");

        if (string.IsNullOrWhiteSpace(Telegram.BotToken))
            errors.Add("Telegram:BotToken cannot be empty.");

        if (Telegram.GetChatIds().Count == 0)
            errors.Add("Telegram:ChatIds cannot be empty.");

        if (string.IsNullOrWhiteSpace(State.NotifiedSymbolsFile))
            errors.Add("State:NotifiedSymbolsFile cannot be empty.");

        if (string.IsNullOrWhiteSpace(State.NotifiedStocksFile))
            errors.Add("State:NotifiedStocksFile cannot be empty.");

        if (string.Equals(State.NotifiedSymbolsFile, State.NotifiedStocksFile, StringComparison.OrdinalIgnoreCase))
            errors.Add("State:NotifiedSymbolsFile and State:NotifiedStocksFile must be different files.");

        if (string.IsNullOrWhiteSpace(State.TokenizedStocksFile))
            errors.Add("State:TokenizedStocksFile cannot be empty.");

        if (Logging.RetentionDays < 0)
            errors.Add("Logging:RetentionDays cannot be negative (0 = keep every log).");

        return errors;
    }
}

internal sealed class BinanceOptions
{
    /// <summary>24-hour ticker. With no query string it returns every symbol.</summary>
    public string Ticker24hUrl { get; set; } = "https://data-api.binance.vision/api/v3/ticker/24hr";

    /// <summary>Exchange info; used to know which symbols are tradable.</summary>
    public string ExchangeInfoUrl { get; set; } = "https://data-api.binance.vision/api/v3/exchangeInfo";

    /// <summary>Quote asset to consider; symbols ending in it are the ones kept.</summary>
    public string QuoteAsset { get; set; } = "USDT";

    /// <summary>
    /// Discards symbols that aren't in TRADING status. Suspended pairs (BREAK/HALT) keep
    /// their 24h stats frozen and generate pump alerts for coins that can't actually be traded.
    /// </summary>
    public bool OnlyTradingSymbols { get; set; } = true;
}

internal sealed class FilterOptions
{
    /// <summary>Minimum 24h gain (in %) for a crypto coin to make it into the alert.</summary>
    public decimal MinChangePercent { get; set; } = 100m;

    /// <summary>
    /// Minimum 24h gain (in %) for a tokenized stock. Equities move far less than crypto
    /// — a real +15% day is exceptional — so they need a much lower threshold to be useful.
    /// </summary>
    public decimal StockMinChangePercent { get; set; } = 20m;
}

internal sealed class StateOptions
{
    /// <summary>
    /// File where already-notified crypto symbols are remembered. If it's a relative path,
    /// it's resolved against the executable's folder, not the working directory.
    /// </summary>
    public string NotifiedSymbolsFile { get; set; } = "notified-symbols.json";

    /// <summary>
    /// Same idea for tokenized stocks, kept in a separate file. They have their own
    /// threshold, so mixing both in one file would make the state hard to reason about.
    /// </summary>
    public string NotifiedStocksFile { get; set; } = "notified-stocks.json";

    /// <summary>
    /// Read-only catalog of base assets that are tokenized stocks (e.g. AAPLB, TSLAB).
    /// See the README for how to regenerate it.
    /// </summary>
    public string TokenizedStocksFile { get; set; } = "tokenized-stocks.json";
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

    /// <summary>
    /// Comma-separated destination chats; the same message is sent to every one of them.
    /// A single string (not a JSON array) so a single environment variable or secret can
    /// override it, e.g. Telegram__ChatIds="-100111,-100222".
    /// </summary>
    public string ChatIds { get; set; } = "";

    public IReadOnlyList<string> GetChatIds() =>
        ChatIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
