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
    private readonly Dictionary<string, AlertRecord> notifiedCrypto = new(cryptoStore.Load(), StringComparer.Ordinal);
    private readonly Dictionary<string, AlertRecord> notifiedStocks = new(stockStore.Load(), StringComparer.Ordinal);

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
        // state file, so a failure sending one doesn't lose the other's progress. Only crypto
        // steps past its threshold in milestones; tokenized stocks keep today's single alert
        // per pump, which a step of 0 collapses back to.
        var sent = await NotifyGroupAsync(CoinKind.Crypto, cryptoThreshold, settings.Filter.CryptoStepPercent, cryptoStore, notifiedCrypto, coins, cancellationToken);
        sent += await NotifyGroupAsync(CoinKind.TokenizedStock, stockThreshold, 0m, stockStore, notifiedStocks, coins, cancellationToken);

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
        decimal step,
        AlertHistoryStore store,
        Dictionary<string, AlertRecord> history,
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
        // its own — that's what let a quick dip and re-cross produce a duplicate alert. A
        // new milestone is not subject to any of this: it always alerts, cooldown or not —
        // the cooldown only governs when a symbol that fell back below threshold is forgotten.
        foreach (var (symbol, record) in history.ToList())
        {
            if (aboveThreshold.Contains(symbol))
                continue;

            var elapsed = now - record.NotifiedAt;
            if (elapsed < cooldown)
            {
                if (firstScan)
                    AppLog.Info($"{symbol} is below the {label} threshold but within the {settings.Filter.CooldownHours:0.##}h cooldown (notified {elapsed.TotalHours:0.0}h ago): kept.");

                continue;
            }

            history.Remove(symbol);
            AppLog.Info($"{symbol} no longer exceeds the {label} threshold (+{threshold:0.##}%) and its cooldown expired: removed from {store.FileName}.");
        }

        // For each candidate, work out the milestone it has reached (the threshold itself,
        // then every `step` above it — 0 for tokenized stocks collapses this to "just the
        // threshold", today's behaviour). Unseen symbol -> notify at that milestone. A legacy
        // entry with no recorded milestone -> the repo's rule is silence over duplication, so
        // it silently adopts the current milestone instead of guessing whether it's "new".
        // Otherwise -> notify only if this milestone is strictly higher than the one on file.
        var toNotify = new List<(Coin Coin, decimal Milestone)>();
        var skipped = new List<string>();

        foreach (var coin in group)
        {
            var milestone = CoinFilter.Milestone(coin.ChangePercent, threshold, step);

            if (!history.TryGetValue(coin.Symbol, out var record))
            {
                toNotify.Add((coin, milestone));
                continue;
            }

            if (record.Milestone is null)
            {
                history[coin.Symbol] = record with { Milestone = milestone };
                AppLog.Info($"{coin.Symbol}: legacy entry adopted at the +{milestone:0.##}% milestone; no message sent.");
                skipped.Add(coin.Symbol);
                continue;
            }

            if (milestone > record.Milestone)
                toNotify.Add((coin, milestone));
            else
                skipped.Add(coin.Symbol);
        }

        if (firstScan && skipped.Count > 0)
            AppLog.Info($"Already notified {label}, skipped: {string.Join(", ", skipped)}");

        if (toNotify.Count == 0)
        {
            store.Save(history);
            return 0;
        }

        AppLog.Info($"{toNotify.Count} new {label}(s) above the threshold (+{threshold:0.##}%):");
        foreach (var (coin, _) in toNotify)
            AppLog.Info($"  {coin.Symbol,-16} 24h: {coin.ChangePercent,8:+0.00;-0.00}%");

        var messages = MessageFormatter.Build(toNotify, threshold);
        foreach (var message in messages)
            await telegram.SendAsync(message, cancellationToken);

        // The timestamp is refreshed only here, when a message actually goes out for a new
        // milestone — the first crossing of the threshold or a later one clearing the next
        // step. A pump that keeps scanning without reaching the next milestone leaves the
        // timestamp untouched, so once it eventually drops below threshold the cooldown still
        // counts from that last real event. That keeps the number of rewrites — and, in the
        // cloud, git commits — bounded by how many milestones actually get hit, not by how
        // many times the loop scans.
        foreach (var (coin, milestone) in toNotify)
        {
            history[coin.Symbol] = new AlertRecord(now, milestone);

            AppLog.Info(milestone > threshold
                ? $"{coin.Symbol} reached the +{milestone:0.##}% milestone (+{coin.ChangePercent:0.00}% in 24h): notified via Telegram."
                : $"{coin.Symbol} exceeded the {label} threshold (+{coin.ChangePercent:0.00}% in 24h): notified via Telegram.");
        }

        // Only saved now: if the send fails, the next scan has to retry.
        store.Save(history);
        AppLog.Info($"{history.Count} {label} symbol(s) remembered in {store.FileName}.");
        return messages.Count;
    }
}
