using System.Text.Json;
using CoslyHighPriceBot.Configuration;
using CoslyHighPriceBot.Models;

namespace CoslyHighPriceBot.Services;

/// <summary>Reads prices and metadata from Binance's public USD-M futures API.</summary>
internal sealed class BinanceClient(HttpClient http, BinanceOptions options)
{
    /// <summary>
    /// Fetches the 24h ticker for every futures symbol (about 750, a few hundred KB).
    /// It's a single call per run, so there's no point paginating or filtering server-side.
    /// </summary>
    public async Task<IReadOnlyList<Ticker24h>> GetAllTickersAsync(CancellationToken cancellationToken)
    {
        var tickers = await GetJsonAsync<List<Ticker24h>>(options.Ticker24hUrl, cancellationToken);
        return tickers ?? throw new InvalidOperationException("Binance returned an empty response.");
    }

    /// <summary>
    /// Status and contract type for every symbol, keyed by symbol. Unlike spot, the futures
    /// exchangeInfo takes no `symbols` filter: it always returns the full catalog, which is
    /// why this is only called once there's at least one candidate worth classifying.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, SymbolInfo>> GetSymbolMetadataAsync(CancellationToken cancellationToken)
    {
        var info = await GetJsonAsync<ExchangeInfo>(options.ExchangeInfoUrl, cancellationToken);

        return (info?.Symbols ?? [])
            .GroupBy(s => s.Symbol, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
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

    /// <summary>Trims the query string so long parameter lists stay out of the log.</summary>
    private static string SafeUrl(string url) => url.Split('?')[0];
}
