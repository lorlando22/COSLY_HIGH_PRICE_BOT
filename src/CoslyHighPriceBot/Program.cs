using CoslyHighPriceBot.Configuration;
using CoslyHighPriceBot.Services;
using Microsoft.Extensions.Configuration;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    // Las variables de entorno van último para que puedan pisar el JSON: es la forma
    // de pasar el token en la nube sin que quede escrito en ningún archivo.
    // Se nombran con doble guión bajo, por ejemplo Telegram__BotToken.
    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .AddEnvironmentVariables()
        .Build();

    var settings = configuration.Get<AppSettings>() ?? new AppSettings();

    var configErrors = settings.Validate();
    if (configErrors.Count > 0)
    {
        ConsoleLog.Info("Configuración inválida en appsettings.json:");
        foreach (var error in configErrors)
            ConsoleLog.Info($"  - {error}");
        return 1;
    }

    var store = new NotifiedSymbolStore(ResolvePath(settings.State.NotifiedSymbolsFile));
    var alreadyNotified = store.Load();
    ConsoleLog.Info($"{alreadyNotified.Count} símbolo(s) avisados en corridas anteriores ({Path.GetFileName(store.FilePath)}).");

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("CoslyHighPriceBot/1.0");

    var binance = new BinanceClient(http, settings.Binance);
    var telegram = new TelegramNotifier(http, settings.Telegram);

    ConsoleLog.Info("Consultando el ticker de 24h de Binance...");
    var tickers = await binance.GetAllTickersAsync(cts.Token);

    var quoteAsset = settings.Binance.QuoteAsset;
    var threshold = settings.Filter.MinChangePercent;
    ConsoleLog.Info($"{tickers.Count} símbolos recibidos, {CoinFilter.CountQuotePairs(tickers, quoteAsset)} son pares {quoteAsset}.");

    var coins = CoinFilter.Filter(tickers, quoteAsset, threshold);

    if (coins.Count > 0 && settings.Binance.OnlyTradingSymbols)
    {
        var tradingSymbols = await binance.GetTradingSymbolsAsync([.. coins.Select(c => c.Symbol)], cts.Token);
        var suspended = coins.Where(c => !tradingSymbols.Contains(c.Symbol)).ToList();

        if (suspended.Count > 0)
            ConsoleLog.Info($"Descartada(s) {suspended.Count} moneda(s) sin trading activo: {string.Join(", ", suspended.Select(c => c.Symbol))}");

        coins = [.. coins.Where(c => tradingSymbols.Contains(c.Symbol))];
    }

    // Estado nuevo: exactamente las que hoy superan el umbral. Las que ya no aparecen
    // se olvidan, así vuelven a avisar si más adelante repiten el pump.
    var currentSymbols = coins.Select(c => c.Symbol).ToHashSet(StringComparer.Ordinal);

    var forgotten = alreadyNotified.Where(s => !currentSymbols.Contains(s)).ToList();
    if (forgotten.Count > 0)
        ConsoleLog.Info($"Bajaron del umbral, se quitan del archivo: {string.Join(", ", forgotten)}");

    var repeated = coins.Where(c => alreadyNotified.Contains(c.Symbol)).ToList();
    if (repeated.Count > 0)
        ConsoleLog.Info($"Ya avisadas, se omiten: {string.Join(", ", repeated.Select(c => c.Symbol))}");

    var toNotify = coins.Where(c => !alreadyNotified.Contains(c.Symbol)).ToList();
    if (toNotify.Count == 0)
    {
        store.Save(currentSymbols);
        ConsoleLog.Info(coins.Count == 0
            ? $"Ninguna moneda superó el umbral (+{threshold:0.##}%). No se envía nada a Telegram."
            : "Ninguna moneda nueva superó el umbral. No se envía nada a Telegram.");
        return 0;
    }

    var windowChanges = await binance.GetWindowChangesAsync(
        [.. toNotify.Select(c => c.Symbol)],
        settings.Binance.ExtraWindows,
        cts.Token);
    toNotify = [.. toNotify.Select(c => c with { WindowChanges = windowChanges[c.Symbol] })];

    ConsoleLog.Info($"{toNotify.Count} moneda(s) nueva(s) por encima del umbral (+{threshold:0.##}%):");
    foreach (var coin in toNotify)
    {
        var windows = string.Join("  ", coin.WindowChanges.Select(w => $"{w.Window}: {w.ChangePercent,8:+0.00;-0.00}%"));
        ConsoleLog.Info($"  {coin.Symbol,-16} 24h: {coin.ChangePercent,8:+0.00;-0.00}%  {windows}");
    }

    var messages = MessageFormatter.Build(toNotify, quoteAsset, threshold);
    foreach (var message in messages)
        await telegram.SendAsync(message, cts.Token);

    ConsoleLog.Info($"Aviso enviado a Telegram ({messages.Count} mensaje(s)).");

    // Se guarda recién ahora: si el envío falla, la próxima corrida tiene que reintentar.
    store.Save(currentSymbols);
    ConsoleLog.Info($"{currentSymbols.Count} símbolo(s) recordados para no repetir el aviso.");
    return 0;
}
catch (OperationCanceledException)
{
    ConsoleLog.Info("Ejecución cancelada.");
    return 1;
}
catch (Exception ex)
{
    ConsoleLog.Error(ex.Message);
    return 1;
}

/// <summary>Las rutas relativas se resuelven contra el ejecutable, no contra el directorio de trabajo.</summary>
static string ResolvePath(string path) =>
    Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
