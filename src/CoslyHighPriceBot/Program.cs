using System.Net;
using CoslyHighPriceBot.Configuration;
using CoslyHighPriceBot.Models;
using CoslyHighPriceBot.Modules;
using CoslyHighPriceBot.Services;
using CoslyHighPriceBot.Services.Exchanges;
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

    // A SocketsHttpHandler with automatic decompression matters here specifically because
    // exchange responses shrink a lot with it (BingX's ticker: ~367 KB -> ~78 KB; Binance's:
    // ~284 KB -> ~64 KB), and this bot fetches several of these every scan.
    using var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("CoslyHighPriceBot/1.0");

    var telegram = new TelegramNotifier(http, settings.Telegram);

    var clients = new List<IExchangeClient>();
    if (settings.Binance.Enabled)
        clients.Add(new BinanceClient(new ExchangeHttp(http, "Binance"), settings.Binance));
    else
        AppLog.Info("Binance is disabled (Binance:Enabled = false).");

    if (settings.Bybit.Enabled)
        clients.Add(new BybitClient(new ExchangeHttp(http, "Bybit"), settings.Bybit));
    else
        AppLog.Info("Bybit is disabled (Bybit:Enabled = false).");

    if (settings.BingX.Enabled)
        clients.Add(new BingXClient(new ExchangeHttp(http, "BingX"), settings.BingX));
    else
        AppLog.Info("BingX is disabled (BingX:Enabled = false).");

    // settings.Validate() guarantees at least one of the three is enabled.
    clients = OrderByPriority(clients, settings.Exchanges.GetPriority());
    var priorityNames = clients.Select(c => c.Name).ToList();
    var instrumentCaches = clients.ToDictionary(c => c.Name, c => new InstrumentCache(c), StringComparer.Ordinal);

    var daily = new DailyPumpModule(
        telegram, settings,
        new AlertHistoryStore(ResolvePath(settings.State.NotifiedSymbolsFile)),
        new AlertHistoryStore(ResolvePath(settings.State.NotifiedStocksFile)));
    daily.LogState();

    // Which symbols were above the lower of the two thresholds last scan, per exchange. The
    // classification/discard lines are worth reading when that set changes and pure noise
    // when it doesn't, and a run scans many times.
    var lastAboveThreshold = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

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
    // state commit when the run fails, so failing over a transient error in scan 3 would
    // throw away the memory of everything already sent and re-announce all of it on the
    // next run. If a later scan succeeded, what's on disk is consistent and worth keeping.
    return lastScanSucceeded ? 0 : 1;

    async Task<bool> RunScanAsync()
    {
        var tickerResults = await Task.WhenAll(clients.Select(FetchTickersAsync));

        var withTickers = new List<(IExchangeClient Client, IReadOnlyList<MarketTicker> Tickers)>();
        for (var i = 0; i < clients.Count; i++)
            if (tickerResults[i] is { } tickers)
                withTickers.Add((clients[i], tickers));

        if (withTickers.Count == 0)
        {
            AppLog.Error("Every enabled exchange failed to respond this scan.");
            return false;
        }

        var anyExchangeFailed = withTickers.Count < clients.Count;

        // exchangeInfo-style catalogs are only fetched once something, anywhere, clears at
        // least the lower of the two thresholds — a quiet scan still costs exactly one ticker
        // call per exchange and no catalog call at all.
        var minChangePercent = Math.Min(settings.Filter.MinChangePercent, settings.Filter.StockMinChangePercent);
        var hasCandidate = withTickers.Any(x => x.Tickers.Any(t => t.ChangePercent >= minChangePercent));

        var quotes = new List<ExchangeQuote>();

        if (hasCandidate)
        {
            var instrumentResults = await Task.WhenAll(withTickers.Select(x => FetchInstrumentsAsync(x.Client)));

            var anyInstrumentsSucceeded = false;
            for (var i = 0; i < withTickers.Count; i++)
            {
                if (instrumentResults[i] is not { } instruments)
                {
                    anyExchangeFailed = true;
                    continue;
                }

                anyInstrumentsSucceeded = true;
                quotes.AddRange(BuildQuotes(withTickers[i].Client, withTickers[i].Tickers, instruments, minChangePercent));
            }

            if (!anyInstrumentsSucceeded)
            {
                // Every exchange that had a ticker this scan also failed its catalog fetch:
                // there's nothing to build a group from, so the scan can't do its job.
                AppLog.Error("No exchange's instrument catalog could be read this scan.");
                return false;
            }
        }

        var groups = PumpAggregator.Group(quotes, priorityNames);

        return await RunModuleAsync("multi-exchange pump", () => daily.RunAsync(groups, anyExchangeFailed, cts.Token));
    }

    async Task<IReadOnlyList<MarketTicker>?> FetchTickersAsync(IExchangeClient client)
    {
        try
        {
            return await client.GetTickersAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not read the {client.Name} ticker: {ex.Message}");
            return null;
        }
    }

    async Task<IReadOnlyDictionary<string, InstrumentInfo>?> FetchInstrumentsAsync(IExchangeClient client)
    {
        try
        {
            return await instrumentCaches[client.Name].GetAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not read the {client.Name} instrument catalog: {ex.Message}");
            return null;
        }
    }

    // Joins one exchange's tickers with its instrument catalog: a tradeable, classified
    // symbol becomes an ExchangeQuote, an untradeable or unclassifiable one is discarded (and
    // logged, but only for candidates above the lower threshold and only when that specific
    // set changed since last scan — the same "bookkeeping once, events on change" rule
    // DailyPumpModule itself always followed).
    List<ExchangeQuote> BuildQuotes(
        IExchangeClient client,
        IReadOnlyList<MarketTicker> tickers,
        IReadOnlyDictionary<string, InstrumentInfo> instruments,
        decimal minChangePercent)
    {
        var aboveThreshold = tickers
            .Where(t => t.ChangePercent >= minChangePercent)
            .Select(t => t.Symbol)
            .ToHashSet(StringComparer.Ordinal);

        var changed = !lastAboveThreshold.TryGetValue(client.Name, out var previous) || !previous.SetEquals(aboveThreshold);
        lastAboveThreshold[client.Name] = aboveThreshold;

        var quotes = new List<ExchangeQuote>();

        foreach (var ticker in tickers)
        {
            if (!instruments.TryGetValue(ticker.Symbol, out var info))
                continue; // Not every ticker symbol appears in the catalog; silently skip, as Binance always did.

            if (!info.Tradeable)
            {
                if (changed && ticker.ChangePercent >= minChangePercent)
                    AppLog.Info($"{client.Name} {ticker.Symbol} (+{ticker.ChangePercent:0.00}%) discarded: {info.DiscardReason}.");

                continue;
            }

            quotes.Add(new ExchangeQuote(
                client.Name, ticker.Symbol, info.Kind!.Value, ticker.ChangePercent,
                ticker.LastPrice, ticker.OpenPrice, ticker.HighPrice, ticker.LowPrice,
                ticker.QuoteVolume, ticker.TradeCount,
                ticker.LastPrice / info.Multiplier, info.Aliases)
            {
                DisplaySymbol = info.DisplaySymbol
            });
        }

        return quotes;
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

/// <summary>An enabled exchange missing from Exchanges:Priority is used last, in registration order.</summary>
static List<IExchangeClient> OrderByPriority(List<IExchangeClient> clients, IReadOnlyList<string> priority)
{
    var index = new Dictionary<string, int>(StringComparer.Ordinal);
    for (var i = 0; i < priority.Count; i++)
        index[priority[i]] = i;

    return clients
        .OrderBy(c => index.TryGetValue(c.Name, out var i) ? i : int.MaxValue)
        .ToList();
}
