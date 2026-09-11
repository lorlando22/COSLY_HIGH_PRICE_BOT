using System.Net;
using System.Text.Json;

namespace CoslyHighPriceBot.Services.Exchanges;

/// <summary>
/// GET-and-deserialize with the 429/418/5xx backoff every exchange client needs, extracted
/// from what used to be Binance-only so Bybit and BingX get the same resilience for free.
/// One instance per exchange (it just carries the exchange's name for its error messages),
/// sharing the single <see cref="HttpClient"/> the whole process uses.
/// </summary>
internal sealed class ExchangeHttp(HttpClient http, string exchangeName)
{
    /// <summary>Attempts per request before a rate limit is reported to the caller.</summary>
    private const int MaxAttempts = 3;

    public async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                // 429 is a warning, 418 is an IP ban that's already started. Both are worth
                // waiting out rather than failing: the next scan is only a minute away and
                // hammering through them is what turns the first into the second.
                if (IsRetryable(response.StatusCode))
                {
                    if (attempt >= MaxAttempts)
                        throw new ExchangeRateLimitException(
                            $"{exchangeName} kept answering {(int)response.StatusCode} after {MaxAttempts} attempts ({SafeUrl(url)}).");

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
                // Context is added so the log says the problem was with this exchange instead
                // of showing a bare, unattributed exception.
                throw new InvalidOperationException($"Error querying {exchangeName} ({SafeUrl(url)}): {ex.Message}", ex);
            }
        }
    }

    /// <summary>429 (too many requests) and 418 (banned for ignoring 429s), plus transient server errors.</summary>
    private static bool IsRetryable(HttpStatusCode status) =>
        (int)status is 429 or 418 or 500 or 502 or 503 or 504;

    /// <summary>The exchange's own Retry-After when it sends one, otherwise 2s, 4s, 8s...</summary>
    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return delta;

        return TimeSpan.FromSeconds(Math.Pow(2, attempt));
    }

    /// <summary>Trims the query string so long parameter lists (and Bybit's pagination cursor) stay out of the log.</summary>
    private static string SafeUrl(string url) => url.Split('?')[0];
}

/// <summary>
/// An exchange is throttling or has banned the IP. Its own type so a scan can be abandoned
/// for this one reason, for this one exchange, without taking down the rest of the run.
/// </summary>
internal sealed class ExchangeRateLimitException(string message) : Exception(message);
