using System.Net;
using System.Text.Json;
using CoslyHighPriceBot.Configuration;
using CoslyHighPriceBot.Models;

namespace CoslyHighPriceBot.Services;

/// <summary>Reads prices and metadata from Binance's public USD-M futures API.</summary>
internal sealed class BinanceClient(HttpClient http, BinanceOptions options)
{
    /// <summary>Attempts per request before a rate limit is reported to the caller.</summary>
    private const int MaxAttempts = 3;

    /// <summary>
    /// Weight consumed in the current minute, as reported by Binance's own header. The
    /// budget is 2400: worth logging once per scan to know how much room is left.
    /// </summary>
    public int? UsedWeightLastMinute { get; private set; }

    /// <summary>
    /// Fetches the 24h ticker for every futures symbol (about 750, a few hundred KB).
    /// It's a single call per scan, so there's no point paginating or filtering server-side.
    /// </summary>
    public async Task<IReadOnlyList<Ticker24h>> GetAllTickersAsync(CancellationToken cancellationToken)
    {
        var tickers = await GetJsonAsync<List<Ticker24h>>(options.Ticker24hUrl, cancellationToken);
        return tickers ?? throw new InvalidOperationException("Binance returned an empty response.");
    }

    /// <summary>
    /// Status and contract type for every symbol, keyed by symbol. Unlike spot, the futures
    /// exchangeInfo takes no `symbols` filter: it always returns the full catalog, which is
    /// why callers go through <see cref="SymbolMetadataCache"/> instead of calling this twice.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, SymbolInfo>> GetSymbolMetadataAsync(CancellationToken cancellationToken)
    {
        var info = await GetJsonAsync<ExchangeInfo>(options.ExchangeInfoUrl, cancellationToken);

        return (info?.Symbols ?? [])
            .GroupBy(s => s.Symbol, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Candles for one symbol, oldest first. The last one is normally still forming, which
    /// callers detect through <see cref="Kline.CloseTime"/> being in the future.
    /// <para>
    /// This is the only endpoint the bot calls per symbol, so it's the only one that can
    /// realistically hit a rate limit. Rows that don't parse are dropped rather than
    /// throwing: one bad candle shouldn't cost the whole scan.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<Kline>> GetKlinesAsync(
        string symbol, string interval, int limit, CancellationToken cancellationToken)
    {
        var url = $"{options.KlinesUrl}?symbol={Uri.EscapeDataString(symbol)}" +
                  $"&interval={Uri.EscapeDataString(interval)}&limit={limit}";

        var rows = await GetJsonAsync<List<JsonElement[]>>(url, cancellationToken) ?? [];

        var klines = new List<Kline>(rows.Count);
        foreach (var row in rows)
        {
            if (Kline.TryFromArray(row, out var kline))
                klines.Add(kline);
        }

        return klines;
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (ReadUsedWeight(response) is { } weight)
                    UsedWeightLastMinute = weight;

                // 429 is a warning, 418 is an IP ban that's already started. Both are worth
                // waiting out rather than failing: the next scan is only a minute away and
                // hammering through them is what turns the first into the second.
                if (IsRetryable(response.StatusCode))
                {
                    if (attempt >= MaxAttempts)
                        throw new BinanceRateLimitException(
                            $"Binance kept answering {(int)response.StatusCode} after {MaxAttempts} attempts ({SafeUrl(url)}).");

                    await Task.Delay(RetryDelay(response, attempt), cancellationToken);
                    continue;
                }

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
    }

    /// <summary>429 (too many requests) and 418 (banned for ignoring 429s), plus transient server errors.</summary>
    private static bool IsRetryable(HttpStatusCode status) =>
        (int)status is 429 or 418 or 500 or 502 or 503 or 504;

    /// <summary>Binance's own Retry-After when it sends one, otherwise 2s, 4s, 8s...</summary>
    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return delta;

        return TimeSpan.FromSeconds(Math.Pow(2, attempt));
    }

    private static int? ReadUsedWeight(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-MBX-USED-WEIGHT-1M", out var values)
        && int.TryParse(values.FirstOrDefault(), out var weight)
            ? weight
            : null;

    /// <summary>Trims the query string so long parameter lists stay out of the log.</summary>
    private static string SafeUrl(string url) => url.Split('?')[0];
}

/// <summary>
/// Binance is throttling or has banned the IP. Its own type so a scan can be abandoned for
/// this one reason without taking down the rest of the run.
/// </summary>
internal sealed class BinanceRateLimitException(string message) : Exception(message);
