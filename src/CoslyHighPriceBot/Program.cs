using CoslyHighPriceBot.Configuration;
using CoslyHighPriceBot.Services;
using Microsoft.Extensions.Configuration;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

AppLog.Info("===== Execution started =====");

int exitCode;
try
{
    exitCode = await RunAsync();
}
catch (OperationCanceledException)
{
    AppLog.Info("Execution cancelled by the user.");
    exitCode = 1;
}
catch (Exception ex)
{
    AppLog.Error($"{ex.GetType().Name}: {ex.Message}");
    exitCode = 1;
}

AppLog.Info($"===== Execution finished (exit code {exitCode}) =====");
return exitCode;

async Task<int> RunAsync()
{
    // Environment variables go last so they can override the JSON: this is how the
    // token is passed in the cloud without ever being written to a file.
    // They're named with a double underscore separator, e.g. Telegram__BotToken.
    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .AddEnvironmentVariables()
        .Build();

    var settings = configuration.Get<AppSettings>() ?? new AppSettings();

    var configErrors = settings.Validate();
    if (configErrors.Count > 0)
    {
        AppLog.Error("Invalid configuration in appsettings.json:");
        foreach (var error in configErrors)
            AppLog.Error($"  - {error}");
        return 1;
    }

    AppLog.DeleteOldFiles(settings.Logging.RetentionDays);

    var store = new NotifiedSymbolStore(ResolvePath(settings.State.NotifiedSymbolsFile));
    var alreadyNotified = store.Load();
    AppLog.Info($"{alreadyNotified.Count} symbol(s) already notified in previous runs ({Path.GetFileName(store.FilePath)}).");

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("CoslyHighPriceBot/1.0");

    var binance = new BinanceClient(http, settings.Binance);
    var telegram = new TelegramNotifier(http, settings.Telegram);

    AppLog.Info("Fetching Binance's 24h ticker...");
    var tickers = await binance.GetAllTickersAsync(cts.Token);

    var quoteAsset = settings.Binance.QuoteAsset;
    var threshold = settings.Filter.MinChangePercent;
    AppLog.Info($"{tickers.Count} symbols received, {CoinFilter.CountQuotePairs(tickers, quoteAsset)} are {quoteAsset} pairs.");

    var coins = CoinFilter.Filter(tickers, quoteAsset, threshold);

    if (coins.Count > 0 && settings.Binance.OnlyTradingSymbols)
    {
        var tradingSymbols = await binance.GetTradingSymbolsAsync([.. coins.Select(c => c.Symbol)], cts.Token);
        var suspended = coins.Where(c => !tradingSymbols.Contains(c.Symbol)).ToList();

        foreach (var coin in suspended)
            AppLog.Info($"{coin.Symbol} exceeded the threshold but isn't tradable (trading suspended): discarded.");

        coins = [.. coins.Where(c => tradingSymbols.Contains(c.Symbol))];
    }

    // New state: exactly the symbols that are above the threshold today. The ones no
    // longer present are forgotten, so they'll be notified again if they pump again later.
    var currentSymbols = coins.Select(c => c.Symbol).ToHashSet(StringComparer.Ordinal);

    foreach (var symbol in alreadyNotified.Where(s => !currentSymbols.Contains(s)))
        AppLog.Info($"{symbol} no longer exceeds the threshold (+{threshold:0.##}%): removed from the notified-symbols file.");

    var repeated = coins.Where(c => alreadyNotified.Contains(c.Symbol)).ToList();
    if (repeated.Count > 0)
        AppLog.Info($"Already notified, skipped: {string.Join(", ", repeated.Select(c => c.Symbol))}");

    var toNotify = coins.Where(c => !alreadyNotified.Contains(c.Symbol)).ToList();
    if (toNotify.Count == 0)
    {
        store.Save(currentSymbols);
        AppLog.Info(coins.Count == 0
            ? $"No coin exceeded the threshold (+{threshold:0.##}%). Nothing sent to Telegram."
            : "No new coin exceeded the threshold. Nothing sent to Telegram.");
        return 0;
    }

    var windowChanges = await binance.GetWindowChangesAsync(
        [.. toNotify.Select(c => c.Symbol)],
        settings.Binance.ExtraWindows,
        cts.Token);
    toNotify = [.. toNotify.Select(c => c with { WindowChanges = windowChanges[c.Symbol] })];

    AppLog.Info($"{toNotify.Count} new coin(s) above the threshold (+{threshold:0.##}%):");
    foreach (var coin in toNotify)
    {
        var windows = string.Join("  ", coin.WindowChanges.Select(w => $"{w.Window}: {w.ChangePercent,8:+0.00;-0.00}%"));
        AppLog.Info($"  {coin.Symbol,-16} 24h: {coin.ChangePercent,8:+0.00;-0.00}%  {windows}");
    }

    var messages = MessageFormatter.Build(toNotify, quoteAsset, threshold);
    foreach (var message in messages)
        await telegram.SendAsync(message, cts.Token);

    foreach (var coin in toNotify)
        AppLog.Info($"{coin.Symbol} exceeded the threshold (+{coin.ChangePercent:0.00}% in 24h): notified via Telegram.");

    // Only saved now: if the send fails, the next run has to retry.
    store.Save(currentSymbols);
    AppLog.Info($"{currentSymbols.Count} symbol(s) remembered to avoid repeating the alert.");
    return 0;
}

/// <summary>Relative paths are resolved against the executable, not the working directory.</summary>
static string ResolvePath(string path) =>
    Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
