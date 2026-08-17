using CoslyHighPriceBot.Configuration;
using CoslyHighPriceBot.Models;
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

    var catalog = new SymbolSetStore(ResolvePath(settings.State.TokenizedStocksFile));
    var tokenizedStockAssets = catalog.Load();
    if (tokenizedStockAssets.Count == 0)
        AppLog.Warn($"{catalog.FileName} is missing or empty: every symbol will be treated as crypto.");
    else
        AppLog.Info($"{tokenizedStockAssets.Count} tokenized stock asset(s) loaded from {catalog.FileName}.");

    var cryptoStore = new SymbolSetStore(ResolvePath(settings.State.NotifiedSymbolsFile));
    var stockStore = new SymbolSetStore(ResolvePath(settings.State.NotifiedStocksFile));
    var notifiedCrypto = cryptoStore.Load();
    var notifiedStocks = stockStore.Load();
    AppLog.Info($"Already notified in previous runs: {notifiedCrypto.Count} crypto ({cryptoStore.FileName}), {notifiedStocks.Count} stock(s) ({stockStore.FileName}).");

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("CoslyHighPriceBot/1.0");

    var binance = new BinanceClient(http, settings.Binance);
    var telegram = new TelegramNotifier(http, settings.Telegram);

    AppLog.Info("Fetching Binance's 24h ticker...");
    var tickers = await binance.GetAllTickersAsync(cts.Token);

    var quoteAsset = settings.Binance.QuoteAsset;
    var cryptoThreshold = settings.Filter.MinChangePercent;
    var stockThreshold = settings.Filter.StockMinChangePercent;
    AppLog.Info($"{tickers.Count} symbols received, {CoinFilter.CountQuotePairs(tickers, quoteAsset)} are {quoteAsset} pairs.");

    var coins = CoinFilter.Filter(tickers, quoteAsset, cryptoThreshold, stockThreshold, tokenizedStockAssets);

    if (coins.Count > 0 && settings.Binance.OnlyTradingSymbols)
    {
        var tradingSymbols = await binance.GetTradingSymbolsAsync([.. coins.Select(c => c.Symbol)], cts.Token);
        var suspended = coins.Where(c => !tradingSymbols.Contains(c.Symbol)).ToList();

        foreach (var coin in suspended)
            AppLog.Info($"{coin.Symbol} exceeded the threshold but isn't tradable (trading suspended): discarded.");

        coins = [.. coins.Where(c => tradingSymbols.Contains(c.Symbol))];
    }

    // Each kind is handled on its own: its own threshold, its own message and its own
    // state file, so a failure sending one doesn't lose the other's progress.
    var sent = await NotifyGroupAsync(CoinKind.Crypto, cryptoThreshold, cryptoStore, notifiedCrypto);
    sent += await NotifyGroupAsync(CoinKind.TokenizedStock, stockThreshold, stockStore, notifiedStocks);

    if (sent > 0)
        AppLog.Info($"Alert sent to Telegram ({sent} message(s)).");
    else if (coins.Count == 0)
        AppLog.Info($"No coin exceeded its threshold (+{cryptoThreshold:0.##}% crypto, +{stockThreshold:0.##}% stocks). Nothing sent to Telegram.");
    else
        AppLog.Info("No new coin exceeded its threshold. Nothing sent to Telegram.");

    return 0;

    async Task<int> NotifyGroupAsync(CoinKind kind, decimal threshold, SymbolSetStore store, IReadOnlySet<string> alreadyNotified)
    {
        var label = kind == CoinKind.TokenizedStock ? "tokenized stock" : "crypto";
        var group = coins.Where(c => c.Kind == kind).ToList();

        // New state: exactly the symbols of this kind above the threshold today. The ones
        // no longer present are forgotten, so they'll be notified again if they pump again.
        var currentSymbols = group.Select(c => c.Symbol).ToHashSet(StringComparer.Ordinal);

        foreach (var symbol in alreadyNotified.Where(s => !currentSymbols.Contains(s)))
            AppLog.Info($"{symbol} no longer exceeds the {label} threshold (+{threshold:0.##}%): removed from {store.FileName}.");

        var repeated = group.Where(c => alreadyNotified.Contains(c.Symbol)).ToList();
        if (repeated.Count > 0)
            AppLog.Info($"Already notified {label}, skipped: {string.Join(", ", repeated.Select(c => c.Symbol))}");

        var toNotify = group.Where(c => !alreadyNotified.Contains(c.Symbol)).ToList();
        if (toNotify.Count == 0)
        {
            store.Save(currentSymbols);
            return 0;
        }

        AppLog.Info($"{toNotify.Count} new {label}(s) above the threshold (+{threshold:0.##}%):");
        foreach (var coin in toNotify)
            AppLog.Info($"  {coin.Symbol,-16} 24h: {coin.ChangePercent,8:+0.00;-0.00}%");

        var messages = MessageFormatter.Build(toNotify, threshold);
        foreach (var message in messages)
            await telegram.SendAsync(message, cts.Token);

        foreach (var coin in toNotify)
            AppLog.Info($"{coin.Symbol} exceeded the {label} threshold (+{coin.ChangePercent:0.00}% in 24h): notified via Telegram.");

        // Only saved now: if the send fails, the next run has to retry.
        store.Save(currentSymbols);
        AppLog.Info($"{currentSymbols.Count} {label} symbol(s) remembered in {store.FileName}.");
        return messages.Count;
    }
}

/// <summary>Relative paths are resolved against the executable, not the working directory.</summary>
static string ResolvePath(string path) =>
    Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
