using System.Globalization;
using System.Text.Json;
using CoslyHighPriceBot.Configuration;
using CoslyHighPriceBot.Models;

namespace CoslyHighPriceBot.Services;

/// <summary>Lee precios y metadatos de la API pública de Binance.</summary>
internal sealed class BinanceClient(HttpClient http, BinanceOptions options)
{
    /// <summary>
    /// Trae el ticker de 24h de todos los símbolos del exchange (unos 3.000, varios MB).
    /// Es una única llamada por ejecución, así que no vale la pena paginar ni filtrar del lado del servidor.
    /// </summary>
    public async Task<IReadOnlyList<Ticker24h>> GetAllTickersAsync(CancellationToken cancellationToken)
    {
        var tickers = await GetJsonAsync<List<Ticker24h>>(options.Ticker24hUrl, cancellationToken);
        return tickers ?? throw new InvalidOperationException("Binance devolvió una respuesta vacía.");
    }

    /// <summary>Devuelve, de los símbolos indicados, cuáles están en estado TRADING.</summary>
    public async Task<IReadOnlySet<string>> GetTradingSymbolsAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken cancellationToken)
    {
        if (symbols.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        var url = $"{options.ExchangeInfoUrl}?symbols={EncodeSymbols(symbols)}";
        var info = await GetJsonAsync<ExchangeInfo>(url, cancellationToken);

        return (info?.Symbols ?? [])
            .Where(s => string.Equals(s.Status, "TRADING", StringComparison.Ordinal))
            .Select(s => s.Symbol)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Variación de cada símbolo en las ventanas pedidas (por ejemplo "4h" y "1h").
    /// Una llamada por ventana, con todos los símbolos juntos. Los símbolos sin operaciones
    /// en la ventana devuelven 0, que es el valor que Binance reporta.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<WindowChange>>> GetWindowChangesAsync(
        IReadOnlyCollection<string> symbols,
        IReadOnlyList<string> windows,
        CancellationToken cancellationToken)
    {
        var result = symbols.ToDictionary(s => s, _ => new List<WindowChange>(), StringComparer.Ordinal);
        if (symbols.Count == 0)
            return result.ToDictionary(p => p.Key, p => (IReadOnlyList<WindowChange>)p.Value, StringComparer.Ordinal);

        foreach (var window in windows)
        {
            var url = $"{options.RollingTickerUrl}?symbols={EncodeSymbols(symbols)}&windowSize={window}";
            var tickers = await GetJsonAsync<List<WindowTicker>>(url, cancellationToken) ?? [];

            var bySymbol = tickers
                .Where(t => result.ContainsKey(t.Symbol))
                .ToDictionary(t => t.Symbol, ParseChangePercent, StringComparer.Ordinal);

            // Un símbolo ausente de la respuesta se muestra como 0%, igual que uno sin operaciones.
            // Se avisa por consola porque son dos situaciones distintas y sin traza no hay forma de distinguirlas.
            var missing = result.Keys.Where(s => !bySymbol.ContainsKey(s)).ToList();
            if (missing.Count > 0)
                AppLog.Warn($"Binance no devolvió datos de {window} para: {string.Join(", ", missing)}. Se muestran como 0%.");

            foreach (var (symbol, changes) in result)
                changes.Add(new WindowChange(window, bySymbol.GetValueOrDefault(symbol)));
        }

        return result.ToDictionary(p => p.Key, p => (IReadOnlyList<WindowChange>)p.Value, StringComparer.Ordinal);
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken);
        }
        // El timeout de HttpClient también llega como TaskCanceledException, pero un Ctrl+C
        // tiene que seguir de largo para que se reporte como cancelación y no como error.
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Se agrega el contexto para que el log diga que el problema fue con Binance
            // y no una excepción suelta sin origen.
            throw new InvalidOperationException($"Error consultando Binance ({SafeUrl(url)}): {ex.Message}", ex);
        }
    }

    /// <summary>Recorta la query string, que puede traer la lista completa de símbolos.</summary>
    private static string SafeUrl(string url) => url.Split('?')[0];

    /// <summary>Binance espera el parámetro symbols como un array JSON dentro de la query string.</summary>
    private static string EncodeSymbols(IReadOnlyCollection<string> symbols) =>
        Uri.EscapeDataString(JsonSerializer.Serialize(symbols));

    private static decimal ParseChangePercent(WindowTicker ticker) =>
        decimal.TryParse(ticker.PriceChangePercent, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0m;
}
