using CoslyHighPriceBot.Models;

namespace CoslyHighPriceBot.Services.Exchanges;

/// <summary>
/// Fetches one exchange's instrument catalog and hands the same dictionary to everyone
/// afterwards — the same idea as the single-exchange bot's SymbolMetadataCache, now with one
/// instance per exchange. It's lazy, so a quiet run (nothing anywhere crosses even the lower
/// of the two thresholds) still costs exactly one ticker call per exchange and no catalog
/// call at all.
/// </summary>
internal sealed class InstrumentCache(IExchangeClient client)
{
    /// <summary>
    /// How long a fetched catalog is trusted. Irrelevant to the cloud's single-pass runs; it
    /// exists for run-bot.cmd's day-long session, where a coin listed after startup would
    /// otherwise be missing from the catalog — and so silently skipped — until the next day.
    /// </summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    private IReadOnlyDictionary<string, InstrumentInfo>? cached;
    private DateTimeOffset fetchedAt;

    public async Task<IReadOnlyDictionary<string, InstrumentInfo>> GetAsync(CancellationToken cancellationToken)
    {
        if (cached is not null && DateTimeOffset.UtcNow - fetchedAt < MaxAge)
            return cached;

        AppLog.Info($"Fetching {client.Name}'s instrument catalog to classify symbols...");
        try
        {
            cached = await client.GetInstrumentsAsync(cancellationToken);
        }
        catch (Exception ex) when (cached is not null && ex is not OperationCanceledException)
        {
            // An hour-old catalog is far better than dropping the exchange from the scan: keep
            // using it and try again next scan.
            AppLog.Warn($"Could not refresh {client.Name}'s instrument catalog ({ex.Message}); reusing the previous one.");
            return cached;
        }

        fetchedAt = DateTimeOffset.UtcNow;
        AppLog.Info($"{client.Name} catalog loaded: {cached.Count} symbols (reused for up to {MaxAge.TotalMinutes:0} minutes).");

        return cached;
    }
}
