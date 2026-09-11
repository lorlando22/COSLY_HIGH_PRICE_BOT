namespace CoslyHighPriceBot.Configuration;

/// <summary>Mirrors appsettings.json. Populated via IConfiguration.Get&lt;AppSettings&gt;().</summary>
internal sealed class AppSettings
{
    public ExchangesOptions Exchanges { get; set; } = new();
    public BinanceOptions Binance { get; set; } = new();
    public BybitOptions Bybit { get; set; } = new();
    public BingXOptions BingX { get; set; } = new();
    public FilterOptions Filter { get; set; } = new();
    public RunOptions Run { get; set; } = new();
    public TelegramOptions Telegram { get; set; } = new();
    public StateOptions State { get; set; } = new();
    public LoggingOptions Logging { get; set; } = new();

    private static readonly string[] KnownExchanges = ["Binance", "Bybit", "BingX"];

    /// <summary>Returns the list of configuration problems. Empty = everything is fine.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        var priority = Exchanges.GetPriority();
        foreach (var name in priority)
            if (!KnownExchanges.Contains(name, StringComparer.Ordinal))
                errors.Add($"Exchanges:Priority has an unknown exchange '{name}'.");

        if (priority.Distinct(StringComparer.Ordinal).Count() != priority.Count)
            errors.Add("Exchanges:Priority cannot list the same exchange twice.");

        if (!Binance.Enabled && !Bybit.Enabled && !BingX.Enabled)
            errors.Add("At least one exchange must be enabled.");

        // Only the enabled exchanges' URLs matter: a disabled one is never called, so a
        // placeholder or blank value there shouldn't block startup.
        if (Binance.Enabled)
        {
            if (!Uri.TryCreate(Binance.Ticker24hUrl, UriKind.Absolute, out _))
                errors.Add("Binance:Ticker24hUrl must be an absolute URL.");

            if (!Uri.TryCreate(Binance.ExchangeInfoUrl, UriKind.Absolute, out _))
                errors.Add("Binance:ExchangeInfoUrl must be an absolute URL.");
        }

        if (Bybit.Enabled)
        {
            if (!Uri.TryCreate(Bybit.TickersUrl, UriKind.Absolute, out _))
                errors.Add("Bybit:TickersUrl must be an absolute URL.");

            if (!Uri.TryCreate(Bybit.InstrumentsUrl, UriKind.Absolute, out _))
                errors.Add("Bybit:InstrumentsUrl must be an absolute URL.");
        }

        if (BingX.Enabled)
        {
            if (!Uri.TryCreate(BingX.TickerUrl, UriKind.Absolute, out _))
                errors.Add("BingX:TickerUrl must be an absolute URL.");

            if (!Uri.TryCreate(BingX.ContractsUrl, UriKind.Absolute, out _))
                errors.Add("BingX:ContractsUrl must be an absolute URL.");
        }

        if (string.IsNullOrWhiteSpace(Binance.QuoteAsset))
            errors.Add("Binance:QuoteAsset cannot be empty (e.g.: USDT).");

        if (Filter.MinChangePercent <= 0)
            errors.Add("Filter:MinChangePercent must be greater than 0.");

        if (Filter.StockMinChangePercent <= 0)
            errors.Add("Filter:StockMinChangePercent must be greater than 0.");

        if (Filter.CooldownHours < 0 || double.IsNaN(Filter.CooldownHours))
            errors.Add("Filter:CooldownHours cannot be negative (0 = no cooldown).");

        if (Filter.CryptoStepPercent < 0)
            errors.Add("Filter:CryptoStepPercent cannot be negative (0 = a single alert per pump).");

        if (Run.IntervalSeconds < 0)
            errors.Add("Run:IntervalSeconds cannot be negative (0 = one scan per run).");

        if (Run.MaxRunMinutes < 0)
            errors.Add("Run:MaxRunMinutes cannot be negative (0 = one scan per run).");

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

        // Two separate memories, one per kind of alert: sharing a file between them would
        // let one kind's cooldown silence the other's.
        string[] stateFiles = [State.NotifiedSymbolsFile, State.NotifiedStocksFile];
        if (stateFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count() != stateFiles.Length)
            errors.Add("State:NotifiedSymbolsFile and State:NotifiedStocksFile must be different files.");

        if (Logging.RetentionDays < 0)
            errors.Add("Logging:RetentionDays cannot be negative (0 = keep every log).");

        return errors;
    }
}

internal sealed class ExchangesOptions
{
    /// <summary>
    /// Comma-separated exchange names, highest priority first: which exchange's
    /// price/open/high/low/volume/trade-count a pump message shows for a coin listed on more
    /// than one, and which state-file key a coin gets the first time it's seen. A plain
    /// string, not a List&lt;string&gt;: the config binder appends to a non-empty list
    /// default instead of replacing it, so a comma-separated string is what lets an
    /// environment variable override it cleanly (Exchanges__Priority). An exchange that's
    /// enabled but missing from this list is used last, in whatever order it was registered.
    /// </summary>
    public string Priority { get; set; } = "Binance,Bybit,BingX";

    public IReadOnlyList<string> GetPriority() =>
        Priority.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

internal sealed class BinanceOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// USD-M futures 24-hour ticker. With no query string it returns every symbol.
    /// Served through www.binance.com on purpose: see the note about 451 in the README.
    /// </summary>
    public string Ticker24hUrl { get; set; } = "https://www.binance.com/fapi/v1/ticker/24hr";

    /// <summary>Futures exchange info: symbol status and contract type. Takes no filters.</summary>
    public string ExchangeInfoUrl { get; set; } = "https://www.binance.com/fapi/v1/exchangeInfo";

    /// <summary>Quote asset to consider; symbols matching it are the ones kept.</summary>
    public string QuoteAsset { get; set; } = "USDT";

    /// <summary>
    /// Discards symbols that aren't in TRADING status. Suspended pairs (BREAK/HALT) keep
    /// their 24h stats frozen and generate pump alerts for coins that can't actually be traded.
    /// </summary>
    public bool OnlyTradingSymbols { get; set; } = true;
}

internal sealed class BybitOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Linear (USDT/USDC-margined) 24h ticker for every symbol.</summary>
    public string TickersUrl { get; set; } = "https://api.bybit.com/v5/market/tickers?category=linear";

    /// <summary>
    /// Instrument catalog: status, contract type, settlement coin and symbol tier. Paginated
    /// (see BybitClient) since the endpoint defaults to 500 per page and Bybit lists more
    /// linear contracts than that.
    /// </summary>
    public string InstrumentsUrl { get; set; } = "https://api.bybit.com/v5/market/instruments-info?category=linear&limit=1000";
}

internal sealed class BingXOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>USDT-margined perpetuals 24h ticker for every symbol.</summary>
    public string TickerUrl { get; set; } = "https://open-api.bingx.com/openApi/swap/v2/quote/ticker";

    /// <summary>Contract catalog: currency, status and display name.</summary>
    public string ContractsUrl { get; set; } = "https://open-api.bingx.com/openApi/swap/v2/quote/contracts";
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

    /// <summary>
    /// Hours to wait before alerting about the same symbol again. Without it, a coin that
    /// crosses the threshold, dips and crosses again counts as new every time and gets
    /// announced two or three times. 0 disables the cooldown. Fractional values are allowed.
    /// </summary>
    public double CooldownHours { get; set; } = 8;

    /// <summary>
    /// Extra gain (in %) between milestone alerts for crypto, on top of MinChangePercent:
    /// with 100 and 50, a coin is announced at +100%, +150%, +200%... once per milestone.
    /// 0 keeps the old behaviour (a single alert per pump). Tokenized stocks never step.
    /// </summary>
    public decimal CryptoStepPercent { get; set; } = 50m;
}

/// <summary>The program's scan loop: how often it scans and for how long a run keeps going.</summary>
internal sealed class RunOptions
{
    /// <summary>
    /// Seconds between scans inside a single run. 0 means one scan and exit, which is how
    /// the bot originally worked. Looping is what makes the 24h module ~13x more responsive
    /// than a single pass on a 15-minute cron would be.
    /// </summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// How long a run keeps scanning before exiting. Kept under the workflow's 15-minute
    /// cron so a run is always finished before the next one is due.
    /// </summary>
    public int MaxRunMinutes { get; set; } = 13;
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
