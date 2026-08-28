namespace CoslyHighPriceBot.Configuration;

/// <summary>Mirrors appsettings.json. Populated via IConfiguration.Get&lt;AppSettings&gt;().</summary>
internal sealed class AppSettings
{
    public BinanceOptions Binance { get; set; } = new();
    public FilterOptions Filter { get; set; } = new();
    public ScanOptions Scan { get; set; } = new();
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

        if (!Uri.TryCreate(Binance.KlinesUrl, UriKind.Absolute, out _))
            errors.Add("Binance:KlinesUrl must be an absolute URL.");

        if (string.IsNullOrWhiteSpace(Binance.QuoteAsset))
            errors.Add("Binance:QuoteAsset cannot be empty (e.g.: USDT).");

        if (Filter.MinChangePercent <= 0)
            errors.Add("Filter:MinChangePercent must be greater than 0.");

        if (Filter.StockMinChangePercent <= 0)
            errors.Add("Filter:StockMinChangePercent must be greater than 0.");

        if (Filter.CooldownHours < 0 || double.IsNaN(Filter.CooldownHours))
            errors.Add("Filter:CooldownHours cannot be negative (0 = no cooldown).");

        errors.AddRange(ValidateScan());

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

        if (string.IsNullOrWhiteSpace(State.NotifiedEarlyFile))
            errors.Add("State:NotifiedEarlyFile cannot be empty.");

        // Three separate memories, one per kind of alert: sharing a file between two of them
        // would let one kind's cooldown silence the other's.
        string[] stateFiles = [State.NotifiedSymbolsFile, State.NotifiedStocksFile, State.NotifiedEarlyFile];
        if (stateFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count() != stateFiles.Length)
            errors.Add("State:NotifiedSymbolsFile, State:NotifiedStocksFile and State:NotifiedEarlyFile must all be different files.");

        if (Logging.RetentionDays < 0)
            errors.Add("Logging:RetentionDays cannot be negative (0 = keep every log).");

        return errors;
    }

    /// <summary>
    /// Checks the early-pump options even when the module is off, so a typo doesn't stay
    /// hidden until the day it gets enabled.
    /// </summary>
    private List<string> ValidateScan()
    {
        var errors = new List<string>();

        if (Scan.IntervalSeconds < 0)
            errors.Add("Scan:IntervalSeconds cannot be negative (0 = one scan per run).");

        if (Scan.MaxRunMinutes < 0)
            errors.Add("Scan:MaxRunMinutes cannot be negative (0 = one scan per run).");

        if (Scan.KlineLimit is < 50 or > 1500)
            errors.Add("Scan:KlineLimit must be between 50 and 1500 (Binance's own cap).");

        if (string.IsNullOrWhiteSpace(Scan.KlineInterval))
            errors.Add("Scan:KlineInterval cannot be empty (e.g.: 5m).");

        if (Scan.BollingerPeriod < 2)
            errors.Add("Scan:BollingerPeriod must be at least 2.");

        if (Scan.BollingerStdDev <= 0)
            errors.Add("Scan:BollingerStdDev must be greater than 0.");

        if (Scan.SqueezeLookback < 1)
            errors.Add("Scan:SqueezeLookback must be at least 1.");

        // The scan needs enough candles for the band window plus the whole squeeze lookback,
        // or every symbol is silently skipped for lack of history.
        if (Scan.SqueezeLookback + Scan.BollingerPeriod >= Scan.KlineLimit)
            errors.Add("Scan:SqueezeLookback + Scan:BollingerPeriod must be smaller than Scan:KlineLimit.");

        if (Scan.SqueezePercentile is <= 0 or > 1)
            errors.Add("Scan:SqueezePercentile must be between 0 (exclusive) and 1 (1 = no squeeze required).");

        if (Scan.VolumeAvgPeriod < 2)
            errors.Add("Scan:VolumeAvgPeriod must be at least 2.");

        if (Scan.VolumeSpikeMultiplier <= 0)
            errors.Add("Scan:VolumeSpikeMultiplier must be greater than 0.");

        if (Scan.RsiPeriod < 2)
            errors.Add("Scan:RsiPeriod must be at least 2.");

        if (Scan.RsiMin >= Scan.RsiMax)
            errors.Add("Scan:RsiMin must be smaller than Scan:RsiMax.");

        if (Scan.MinQuoteVolume24h < 0)
            errors.Add("Scan:MinQuoteVolume24h cannot be negative.");

        if (Scan.MaxQuoteVolume24h < 0)
            errors.Add("Scan:MaxQuoteVolume24h cannot be negative (0 = no ceiling).");

        if (Scan.MaxQuoteVolume24h > 0 && Scan.MaxQuoteVolume24h <= Scan.MinQuoteVolume24h)
            errors.Add("Scan:MaxQuoteVolume24h must be greater than Scan:MinQuoteVolume24h (0 = no ceiling).");

        if (Scan.MaxSymbols < 1)
            errors.Add("Scan:MaxSymbols must be at least 1.");

        if (Scan.MaxConcurrentRequests is < 1 or > 20)
            errors.Add("Scan:MaxConcurrentRequests must be between 1 and 20.");

        if (Scan.CooldownHours < 0 || double.IsNaN(Scan.CooldownHours))
            errors.Add("Scan:CooldownHours cannot be negative (0 = no cooldown).");

        return errors;
    }
}

internal sealed class BinanceOptions
{
    /// <summary>
    /// USD-M futures 24-hour ticker. With no query string it returns every symbol.
    /// Served through www.binance.com on purpose: see the note about 451 in the README.
    /// </summary>
    public string Ticker24hUrl { get; set; } = "https://www.binance.com/fapi/v1/ticker/24hr";

    /// <summary>Futures exchange info: symbol status and contract type. Takes no filters.</summary>
    public string ExchangeInfoUrl { get; set; } = "https://www.binance.com/fapi/v1/exchangeInfo";

    /// <summary>
    /// Candles, one call per symbol. Only the early-pump module uses this; the symbol,
    /// interval and limit are appended as a query string.
    /// </summary>
    public string KlinesUrl { get; set; } = "https://www.binance.com/fapi/v1/klines";

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

    /// <summary>
    /// Hours to wait before alerting about the same symbol again. Without it, a coin that
    /// crosses the threshold, dips and crosses again counts as new every time and gets
    /// announced two or three times. 0 disables the cooldown. Fractional values are allowed.
    /// </summary>
    public double CooldownHours { get; set; } = 8;
}

/// <summary>
/// The early-pump module: instead of waiting for a 24h total, it looks at intraday candles
/// for the moment a move starts — a Bollinger squeeze breaking upwards on a volume spike
/// with RSI confirming. Crypto only; tokenized stocks never take part.
/// <para>
/// The defaults are the configuration measured over 166 symbols x 1000 five-minute candles
/// (about 3.5 days): roughly 12 alerts a day. Loosening the squeeze alone takes that to 53,
/// and dropping the bands and RSI takes it to 93 — it is the squeeze that makes the module
/// usable rather than a firehose.
/// </para>
/// </summary>
internal sealed class ScanOptions
{
    /// <summary>Turns the whole module off, leaving the 24h alerts exactly as they were.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Seconds between scans inside a single run. 0 means one scan and exit, which is how
    /// the bot behaved before this module existed.
    /// </summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// How long a run keeps scanning before exiting. Kept under the workflow's 15-minute
    /// cron so a run is always finished before the next one is due.
    /// </summary>
    public int MaxRunMinutes { get; set; } = 13;

    /// <summary>Candle size. Five minutes is short enough to catch the start of a move and long enough not to be noise.</summary>
    public string KlineInterval { get; set; } = "5m";

    /// <summary>
    /// Candles per symbol. Has to cover SqueezeLookback + BollingerPeriod with room to
    /// spare. Binance charges weight 1 up to 100 candles and 2 up to 500, so 150 is cheap.
    /// </summary>
    public int KlineLimit { get; set; } = 150;

    /// <summary>
    /// Liquidity floor, in quote asset, over the last 24h. This is the pre-filter that keeps
    /// the number of candle calls sane. Binance doesn't publish market cap, and 24h volume
    /// predicts pumps better anyway — it also comes free with the ticker already downloaded.
    /// At 5,000,000 USDT this leaves roughly 169 of the 524 crypto perpetuals.
    /// </summary>
    public decimal MinQuoteVolume24h { get; set; } = 5_000_000m;

    /// <summary>Optional ceiling to skip the mega caps that never double. 0 = no ceiling.</summary>
    public decimal MaxQuoteVolume24h { get; set; }

    /// <summary>Hard cap on candle calls per scan: the most liquid symbols are kept.</summary>
    public int MaxSymbols { get; set; } = 200;

    /// <summary>Candle requests in flight at once. Binance allows 2400 weight a minute; this stays far below it.</summary>
    public int MaxConcurrentRequests { get; set; } = 6;

    public int BollingerPeriod { get; set; } = 20;
    public decimal BollingerStdDev { get; set; } = 2m;

    /// <summary>Candles the squeeze is measured against. 96 five-minute candles is 8 hours.</summary>
    public int SqueezeLookback { get; set; } = 96;

    /// <summary>
    /// How tight the bands must have been just before the breakout, as a fraction of the
    /// lookback: 0.20 means "in the tightest fifth of the last 8 hours". 1 disables the test.
    /// </summary>
    public decimal SqueezePercentile { get; set; } = 0.20m;

    /// <summary>Candles averaged for the volume baseline. The triggering candle is excluded from its own average.</summary>
    public int VolumeAvgPeriod { get; set; } = 20;

    /// <summary>How many times the baseline the triggering candle's volume has to be.</summary>
    public decimal VolumeSpikeMultiplier { get; set; } = 3m;

    public int RsiPeriod { get; set; } = 14;

    /// <summary>Floor for RSI: below this the move isn't convincing.</summary>
    public decimal RsiMin { get; set; } = 60m;

    /// <summary>Ceiling for RSI: above this the move is already exhausted.</summary>
    public decimal RsiMax { get; set; } = 85m;

    /// <summary>Minimum open-to-close move of the triggering candle, in %.</summary>
    public decimal MinCandleBodyPercent { get; set; } = 1.5m;

    /// <summary>
    /// Also test the candle still forming, using its volume so far against the average of
    /// whole candles. That comparison can only understate a spike, so it doesn't invent
    /// signals, and it drops the delay from a full candle to one scan interval. Set to false
    /// to stay strictly on the closed-candle path the thresholds were measured on.
    /// </summary>
    public bool EvaluateFormingCandle { get; set; } = true;

    /// <summary>
    /// Hours before the same symbol can set off an early alert again. Shorter than the 24h
    /// module's, since this fires on a moment rather than on a state that lasts all day.
    /// </summary>
    public double CooldownHours { get; set; } = 2;
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
    /// Third memory, for the early-pump module. Separate from the other two because its
    /// cooldown is much shorter and its alerts are about a different thing entirely.
    /// </summary>
    public string NotifiedEarlyFile { get; set; } = "notified-early.json";
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

    /// <summary>
    /// Destination chats for the early-pump alerts, so they can live in their own channel.
    /// Left empty they fall back to <see cref="ChatIds"/> — that's how the two streams get
    /// merged back into one channel later, by clearing a value rather than changing code.
    /// </summary>
    public string EarlyChatIds { get; set; } = "";

    public IReadOnlyList<string> GetChatIds() => Split(ChatIds);

    public IReadOnlyList<string> GetEarlyChatIds() =>
        string.IsNullOrWhiteSpace(EarlyChatIds) ? GetChatIds() : Split(EarlyChatIds);

    private static string[] Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
