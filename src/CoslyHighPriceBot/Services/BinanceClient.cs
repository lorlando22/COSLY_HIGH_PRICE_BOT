using System.Globalization;
using System.Text.Json;
using CoslyHighPriceBot.Configuration;
using CoslyHighPriceBot.Models;

namespace CoslyHighPriceBot.Services;

/// <summary>Reads prices and metadata from Binance's public API.</summary>
internal sealed class BinanceClient(HttpClient http, BinanceOptions options)
{
    /// <summary>
    /// Fetches the 24h ticker for every symbol on the exchange (about 3,000, several MB).
    /// It's a single call per run, so there's no point paginating or filtering server-side.
    /// </summary>
    public async Task<IReadOnlyList<Ticker24h>> GetAllTickersAsync(CancellationToken cancellationToken)
    {
        var tickers = await GetJsonAsync<List<Ticker24h>>(options.Ticker24hUrl, cancellationToken);
        return tickers ?? throw new InvalidOperationException("Binance returned an empty response.");
    }

    /// <summary>Of the given symbols, returns which ones are in TRADING status.</summary>
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
    /// Change percent for each symbol across the requested windows (e.g. "4h" and "1h").
    /// One call per window, with all symbols batched together. Symbols with no trades in
    /// the window come back as 0, which is the value Binance reports.
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

            // A symbol missing from the response is shown as 0%, same as one with no trades.
            // It's logged because these are two different situations and without a trace there's no way to tell them apart.
            var missing = result.Keys.Where(s => !bySymbol.ContainsKey(s)).ToList();
            if (missing.Count > 0)
                AppLog.Warn($"Binance returned no {window} data for: {string.Join(", ", missing)}. Shown as 0%.");

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
        // HttpClient's timeout also arrives as TaskCanceledException, but a Ctrl+C has to
        // pass through so it's reported as a cancellation, not an error.
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Context is added so the log says the problem was with Binance instead of
            // showing a bare, unattributed exception.
            throw new InvalidOperationException($"Error querying Binance ({SafeUrl(url)}): {ex.Message}", ex);
        }
    }

    /// <summary>Trims the query string, which can carry the full list of symbols.</summary>
    private static string SafeUrl(string url) => url.Split('?')[0];

    /// <summary>Binance expects the symbols parameter as a JSON array inside the query string.</summary>
    private static string EncodeSymbols(IReadOnlyCollection<string> symbols) =>
        Uri.EscapeDataString(JsonSerializer.Serialize(symbols));

    private static decimal ParseChangePercent(WindowTicker ticker) =>
        decimal.TryParse(ticker.PriceChangePercent, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0m;
}
