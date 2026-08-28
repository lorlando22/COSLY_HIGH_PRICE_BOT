using CoslyHighPriceBot.Configuration;
using CoslyHighPriceBot.Models;
using CoslyHighPriceBot.Services;

namespace CoslyHighPriceBot.Modules;

/// <summary>
/// The original detector: symbols whose 24-hour change cleared their threshold, crypto and
/// tokenized stocks each with their own threshold, message and memory. It reports a move
/// that has already happened, which is exactly what the early-pump module was added to
/// complement — not replace.
/// </summary>
internal sealed class DailyPumpModule(
    TelegramNotifier telegram,
    SymbolMetadataCache metadataCache,
    AppSettings settings,
    AlertHistoryStore cryptoStore,
    AlertHistoryStore stockStore)
{
    private readonly Dictionary<string, DateTimeOffset> notifiedCrypto = new(cryptoStore.Load(), StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> notifiedStocks = new(stockStore.Load(), StringComparer.Ordinal);

    /// <summary>
    /// A run scans many times. The bookkeeping lines are worth reading once, not once a
    /// minute, so after the first scan only real events get logged.
    /// </summary>
    private bool firstScan = true;

    /// <summary>
    /// Which symbols were candidates last scan. The classification lines are worth reading
    /// when the set changes and pure noise when it doesn't, and a run scans many times.
    /// </summary>
    private HashSet<string>? lastCandidates;

    public void LogState() =>
        AppLog.Info($"Already notified in previous runs: {notifiedCrypto.Count} crypto ({cryptoStore.FileName}), " +
                    $"{notifiedStocks.Count} stock(s) ({stockStore.FileName}).");

    /// <summary>Returns how many Telegram messages went out.</summary>
    public async Task<int> RunAsync(IReadOnlyList<Ticker24h> tickers, CancellationToken cancellationToken)
    {
        var quoteAsset = settings.Binance.QuoteAsset;
        var cryptoThreshold = settings.Filter.MinChangePercent;
        var stockThreshold = settings.Filter.StockMinChangePercent;

        if (firstScan)
            AppLog.Info($"{tickers.Count} symbols received, {CoinFilter.CountQuotePairs(tickers, quoteAsset)} are {quoteAsset} pairs.");

        // First pass uses the lower of the two thresholds, since a symbol's kind — and so the
        // threshold that really applies — isn't known until exchangeInfo has been read.
        var candidates = CoinFilter.FindCandidates(tickers, quoteAsset, Math.Min(cryptoThreshold, stockThreshold));

        var candidateSymbols = candidates.Select(c => c.Symbol).ToHashSet(StringComparer.Ordinal);
        var candidatesChanged = lastCandidates is null || !lastCandidates.SetEquals(candidateSymbols);
        lastCandidates = candidateSymbols;

        IReadOnlyList<Coin> coins = [];
        if (candidates.Count > 0)
        {
            if (candidatesChanged)
                AppLog.Info($"{candidates.Count} symbol(s) above the lower threshold (+{Math.Min(cryptoThreshold, stockThreshold):0.##}%); classifying them...");

            var metadata = await metadataCache.GetAsync(cancellationToken);
            coins = CoinFilter.Classify(candidates, metadata, cryptoThreshold, stockThreshold,
                settings.Binance.OnlyTradingSymbols, out var discarded);

            if (candidatesChanged)
            {
                foreach (var (coin, reason) in discarded)
                    AppLog.Info($"{coin.Symbol} (+{coin.ChangePercent:0.00}%) discarded: {reason}.");
            }
        }

        // Each kind is handled on its own: its own threshold, its own message and its own
        // state file, so a failure sending one doesn't lose the other's progress.
        var sent = await NotifyGroupAsync(CoinKind.Crypto, cryptoThreshold, cryptoStore, notifiedCrypto, coins, cancellationToken);
        sent += await NotifyGroupAsync(CoinKind.TokenizedStock, stockThreshold, stockStore, notifiedStocks, coins, cancellationToken);

        if (sent > 0)
            AppLog.Info($"24h alert sent to Telegram ({sent} message(s)).");
        else if (firstScan && coins.Count == 0)
            AppLog.Info($"No coin exceeded its 24h threshold (+{cryptoThreshold:0.##}% crypto, +{stockThreshold:0.##}% stocks). Nothing sent to Telegram.");
        else if (firstScan)
            AppLog.Info("No new coin exceeded its 24h threshold. Nothing sent to Telegram.");

        firstScan = false;
        return sent;
    }

    private async Task<int> NotifyGroupAsync(
        CoinKind kind,
        decimal threshold,
        AlertHistoryStore store,
        Dictionary<string, DateTimeOffset> history,
        IReadOnlyList<Coin> coins,
        CancellationToken cancellationToken)
    {
        var label = kind == CoinKind.TokenizedStock ? "tokenized stock" : "crypto";
        var group = coins.Where(c => c.Kind == kind).ToList();
        var aboveThreshold = group.Select(c => c.Symbol).ToHashSet(StringComparer.Ordinal);

        var now = DateTimeOffset.UtcNow;
        var cooldown = TimeSpan.FromHours(settings.Filter.CooldownHours);

        // An entry survives while the symbol is still above the threshold OR while its
        // cooldown is running. Dropping below the threshold no longer clears the memory on
        // its own — that's what let a quick dip and re-cross produce a duplicate alert.
        foreach (var (symbol, notifiedAt) in history.ToList())
        {
            if (aboveThreshold.Contains(symbol))
                continue;

            var elapsed = now - notifiedAt;
            if (elapsed < cooldown)
            {
                if (firstScan)
                    AppLog.Info($"{symbol} is below the {label} threshold but within the {settings.Filter.CooldownHours:0.##}h cooldown (notified {elapsed.TotalHours:0.0}h ago): kept.");

                continue;
            }

            history.Remove(symbol);
            AppLog.Info($"{symbol} no longer exceeds the {label} threshold (+{threshold:0.##}%) and its cooldown expired: removed from {store.FileName}.");
        }

        if (firstScan)
        {
            var repeated = group.Where(c => history.ContainsKey(c.Symbol)).ToList();
            if (repeated.Count > 0)
                AppLog.Info($"Already notified {label}, skipped: {string.Join(", ", repeated.Select(c => c.Symbol))}");
        }

        var toNotify = group.Where(c => !history.ContainsKey(c.Symbol)).ToList();
        if (toNotify.Count == 0)
        {
            store.Save(history);
            return 0;
        }

        AppLog.Info($"{toNotify.Count} new {label}(s) above the threshold (+{threshold:0.##}%):");
        foreach (var coin in toNotify)
            AppLog.Info($"  {coin.Symbol,-16} 24h: {coin.ChangePercent,8:+0.00;-0.00}%");

        var messages = MessageFormatter.Build(toNotify, threshold);
        foreach (var message in messages)
            await telegram.SendAsync(message, cancellationToken);

        // The timestamp records when the message went out and is never refreshed later:
        // refreshing it would extend the cooldown forever on a sustained pump, and would
        // rewrite the file on every run — one git commit every 15 minutes in the cloud.
        foreach (var coin in toNotify)
        {
            history[coin.Symbol] = now;
            AppLog.Info($"{coin.Symbol} exceeded the {label} threshold (+{coin.ChangePercent:0.00}% in 24h): notified via Telegram.");
        }

        // Only saved now: if the send fails, the next scan has to retry.
        store.Save(history);
        AppLog.Info($"{history.Count} {label} symbol(s) remembered in {store.FileName}.");
        return messages.Count;
    }
}
