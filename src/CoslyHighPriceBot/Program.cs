using CoslyHighPriceBot.Configuration;
using CoslyHighPriceBot.Models;
using CoslyHighPriceBot.Modules;
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

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("CoslyHighPriceBot/1.0");

    var binance = new BinanceClient(http, settings.Binance);
    var telegram = new TelegramNotifier(http, settings.Telegram);
    var metadata = new SymbolMetadataCache(binance);

    var daily = new DailyPumpModule(
        telegram, metadata, settings,
        new AlertHistoryStore(ResolvePath(settings.State.NotifiedSymbolsFile)),
        new AlertHistoryStore(ResolvePath(settings.State.NotifiedStocksFile)));
    daily.LogState();

    // A run either scans once and exits — the way the bot always worked — or keeps scanning
    // for a while. Looping is what makes the 24h module ~13x more responsive than a single
    // pass on a 15-minute cron would be.
    var interval = TimeSpan.FromSeconds(settings.Run.IntervalSeconds);
    var looping = interval > TimeSpan.Zero && settings.Run.MaxRunMinutes > 0;
    var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(settings.Run.MaxRunMinutes);

    if (looping)
        AppLog.Info($"Scanning every {interval.TotalSeconds:0}s for up to {settings.Run.MaxRunMinutes} minutes.");

    var scan = 0;
    bool lastScanSucceeded;

    while (true)
    {
        scan++;
        if (looping)
            AppLog.Info($"----- Scan #{scan} -----");

        lastScanSucceeded = await RunScanAsync();

        if (!looping)
            break;

        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= interval)
        {
            AppLog.Info($"Scan window closed after {scan} scan(s).");
            break;
        }

        await Task.Delay(interval, cts.Token);
    }

    // Only the last scan decides the exit code, and that's deliberate. The workflow skips the
    // state commit when the run fails, so failing over a transient Telegram error in scan 3
    // would throw away the memory of everything already sent and re-announce all of it on the
    // next run. If a later scan succeeded, what's on disk is consistent and worth keeping.
    return lastScanSucceeded ? 0 : 1;

    async Task<bool> RunScanAsync()
    {
        IReadOnlyList<Ticker24h> tickers;
        try
        {
            tickers = await binance.GetAllTickersAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not read the Binance ticker: {ex.Message}");
            return false;
        }

        return await RunModuleAsync("24h pump", () => daily.RunAsync(tickers, cts.Token));
    }

    async Task<bool> RunModuleAsync(string name, Func<Task<int>> module)
    {
        try
        {
            await module();
            return true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error($"The {name} module failed: {ex.Message}");
            return false;
        }
    }
}

/// <summary>Relative paths are resolved against the executable, not the working directory.</summary>
static string ResolvePath(string path) =>
    Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
