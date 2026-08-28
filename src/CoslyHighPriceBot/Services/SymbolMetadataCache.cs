using CoslyHighPriceBot.Models;

namespace CoslyHighPriceBot.Services;

/// <summary>
/// Fetches exchangeInfo once and hands the same dictionary to everyone afterwards.
/// <para>
/// It matters for two reasons. The response is about 1 MB and the futures endpoint accepts
/// no filter, so re-fetching it on every scan of a 13-minute run would be the single most
/// wasteful thing the bot does. And because it's lazy, a quiet run with the early-pump
/// module off still costs exactly one Binance call, the way it always has.
/// </para>
/// The catalog changing mid-run is not a concern: a symbol listed or delisted while the
/// process is alive is picked up by the next run, minutes later.
/// </summary>
internal sealed class SymbolMetadataCache(BinanceClient binance)
{
    private IReadOnlyDictionary<string, SymbolInfo>? cached;

    public bool IsLoaded => cached is not null;

    public async Task<IReadOnlyDictionary<string, SymbolInfo>> GetAsync(CancellationToken cancellationToken)
    {
        if (cached is not null)
            return cached;

        AppLog.Info("Fetching exchangeInfo to classify symbols...");
        cached = await binance.GetSymbolMetadataAsync(cancellationToken);
        AppLog.Info($"exchangeInfo loaded: {cached.Count} symbols (cached for the rest of the run).");

        return cached;
    }
}
