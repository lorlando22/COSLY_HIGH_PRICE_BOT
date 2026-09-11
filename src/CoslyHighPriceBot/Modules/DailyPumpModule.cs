using CoslyHighPriceBot.Configuration;
using CoslyHighPriceBot.Models;
using CoslyHighPriceBot.Services;

namespace CoslyHighPriceBot.Modules;

/// <summary>
/// The original detector: coins whose 24-hour change cleared their threshold on at least one
/// exchange, crypto and tokenized stocks each with their own threshold, message and memory.
/// It reports a move that has already happened, which is exactly what the early-pump module
/// was added to complement — not replace. Program.cs and <see cref="PumpAggregator"/> do the
/// work of turning every exchange's raw tickers into cross-exchange <see cref="CoinGroup"/>s;
/// this module only decides, per group, whether that's worth a Telegram message.
/// </summary>
internal sealed class DailyPumpModule(
    TelegramNotifier telegram,
    AppSettings settings,
    AlertHistoryStore cryptoStore,
    AlertHistoryStore stockStore)
{
    private readonly Dictionary<string, AlertRecord> notifiedCrypto = new(
        AlertHistoryStore.MigrateLegacyKeys(cryptoStore.Load(), settings.Binance.QuoteAsset, normalizeMultiplier: true),
        StringComparer.Ordinal);

    private readonly Dictionary<string, AlertRecord> notifiedStocks = new(
        AlertHistoryStore.MigrateLegacyKeys(stockStore.Load(), settings.Binance.QuoteAsset, normalizeMultiplier: false),
        StringComparer.Ordinal);

    /// <summary>
    /// A run scans many times. The bookkeeping lines (already-notified lists, cooldown notes)
    /// are worth reading once, not once a minute, so after the first scan only real events
    /// get logged.
    /// </summary>
    private bool firstScan = true;

    public void LogState() =>
        AppLog.Info($"Already notified in previous runs: {notifiedCrypto.Count} crypto ({cryptoStore.FileName}), " +
                    $"{notifiedStocks.Count} stock(s) ({stockStore.FileName}).");

    /// <summary>
    /// Returns how many Telegram messages went out. <paramref name="anyExchangeFailed"/>
    /// comes from Program.cs: when it's true, a group's absence this scan might just mean its
    /// exchange couldn't be read, not that the coin actually fell below threshold, so the
    /// pruning pass that forgets old entries is skipped entirely for this scan.
    /// </summary>
    public async Task<int> RunAsync(IReadOnlyList<CoinGroup> groups, bool anyExchangeFailed, CancellationToken cancellationToken)
    {
        var cryptoThreshold = settings.Filter.MinChangePercent;
        var stockThreshold = settings.Filter.StockMinChangePercent;

        // Each kind is handled on its own: its own threshold, its own message and its own
        // state file, so a failure sending one doesn't lose the other's progress. Only crypto
        // steps past its threshold in milestones; tokenized stocks keep today's single alert
        // per pump, which a step of 0 collapses back to.
        var sent = await NotifyGroupAsync(CoinKind.Crypto, cryptoThreshold, settings.Filter.CryptoStepPercent, cryptoStore, notifiedCrypto, groups, anyExchangeFailed, cancellationToken);
        sent += await NotifyGroupAsync(CoinKind.TokenizedStock, stockThreshold, 0m, stockStore, notifiedStocks, groups, anyExchangeFailed, cancellationToken);

        if (sent > 0)
            AppLog.Info($"24h alert sent to Telegram ({sent} message(s)).");
        else if (firstScan)
            AppLog.Info($"No coin exceeded its 24h threshold (+{cryptoThreshold:0.##}% crypto, +{stockThreshold:0.##}% stocks). Nothing sent to Telegram.");

        firstScan = false;
        return sent;
    }

    private async Task<int> NotifyGroupAsync(
        CoinKind kind,
        decimal threshold,
        decimal step,
        AlertHistoryStore store,
        Dictionary<string, AlertRecord> history,
        IReadOnlyList<CoinGroup> groups,
        bool anyExchangeFailed,
        CancellationToken cancellationToken)
    {
        var label = kind == CoinKind.TokenizedStock ? "tokenized stock" : "crypto";

        var qualifying = new List<(CoinGroup Group, decimal Best)>();
        foreach (var group in groups)
        {
            if (group.Kind != kind)
                continue;

            if (group.BestQualifyingChangePercent(threshold) is { } best)
                qualifying.Add((group, best));
        }

        // Highest gain first: that's the order the message lists coins in, as it always has.
        qualifying.Sort((a, b) => b.Best.CompareTo(a.Best));

        var now = DateTimeOffset.UtcNow;
        var cooldown = TimeSpan.FromHours(settings.Filter.CooldownHours);

        // An entry survives while the symbol is still above the threshold on some exchange OR
        // while its cooldown is running. Dropping below the threshold no longer clears the
        // memory on its own — that's what let a quick dip and re-cross produce a duplicate
        // alert. A new milestone is not subject to any of this: it always alerts, cooldown or
        // not — the cooldown only governs when a symbol that fell back below threshold is
        // forgotten.
        if (anyExchangeFailed)
        {
            if (firstScan)
                AppLog.Info($"An exchange failed to respond this scan: skipping the {label} pruning pass (missing data isn't the same as falling below the threshold).");
        }
        else
        {
            var aliveAliases = qualifying.SelectMany(x => x.Group.Aliases).ToHashSet(StringComparer.Ordinal);

            foreach (var (symbol, record) in history.ToList())
            {
                if (aliveAliases.Contains(symbol))
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
        }

        // For each qualifying group, work out the milestone it has reached (the threshold
        // itself, then every `step` above it — 0 for tokenized stocks collapses this to "just
        // the threshold", today's behaviour), then look the group up in the history by ANY of
        // its aliases — the exchange whose name ended up on file might not be the group's
        // current highest-priority one. Unseen -> notify at that milestone. A legacy entry
        // with no recorded milestone -> the repo's rule is silence over duplication, so it
        // silently adopts the current milestone instead of guessing whether it's "new".
        // Otherwise -> notify only if this milestone is strictly higher than the one on file.
        var toNotify = new List<(CoinGroup Group, decimal Milestone, string? ExistingKey)>();
        var skipped = new List<string>();

        foreach (var (group, best) in qualifying)
        {
            var milestone = CoinFilter.Milestone(best, threshold, step);
            var (foundKey, foundRecord) = FindHistoryEntry(group.Aliases, history);

            if (foundKey is null)
            {
                toNotify.Add((group, milestone, null));
                continue;
            }

            if (foundRecord!.Value.Milestone is null)
            {
                history[foundKey] = foundRecord.Value with { Milestone = milestone };
                AppLog.Info($"{group.Key}: legacy entry ({foundKey}) adopted at the +{milestone:0.##}% milestone; no message sent.");
                skipped.Add(group.Key);
                continue;
            }

            if (milestone > foundRecord.Value.Milestone)
                toNotify.Add((group, milestone, foundKey));
            else
                skipped.Add(group.Key);
        }

        if (firstScan && skipped.Count > 0)
            AppLog.Info($"Already notified {label}, skipped: {string.Join(", ", skipped)}");

        if (toNotify.Count == 0)
        {
            store.Save(history);
            return 0;
        }

        AppLog.Info($"{toNotify.Count} new {label}(s) above the threshold (+{threshold:0.##}%):");
        foreach (var (group, _, _) in toNotify)
            AppLog.Info($"  {group.Key,-16} 24h: {group.BestQualifyingChangePercent(threshold),8:+0.00;-0.00}%");

        var messages = MessageFormatter.Build(
            toNotify.Select(x => (x.Group, x.Milestone)).ToList(), threshold, settings.Binance.QuoteAsset);
        foreach (var message in messages)
            await telegram.SendAsync(message, cancellationToken);

        // The timestamp is refreshed only here, when a message actually goes out for a new
        // milestone — the first crossing of the threshold or a later one clearing the next
        // step. A pump that keeps scanning without reaching the next milestone leaves the
        // timestamp untouched, so once it eventually drops below threshold the cooldown still
        // counts from that last real event. That keeps the number of rewrites — and, in the
        // cloud, git commits — bounded by how many milestones actually get hit, not by how
        // many times the loop scans.
        //
        // The key updated is whichever one the alias lookup found (stable across a run where
        // the group's highest-priority exchange changes), or the group's own key when this is
        // the first time the coin is seen.
        foreach (var (group, milestone, existingKey) in toNotify)
        {
            var key = existingKey ?? group.Key;
            history[key] = new AlertRecord(now, milestone);

            AppLog.Info(milestone > threshold
                ? $"{group.Key} reached the +{milestone:0.##}% milestone (+{group.BestQualifyingChangePercent(threshold):0.00}% in 24h): notified via Telegram."
                : $"{group.Key} exceeded the {label} threshold (+{group.BestQualifyingChangePercent(threshold):0.00}% in 24h): notified via Telegram.");
        }

        // Only saved now: if the send fails, the next scan has to retry.
        store.Save(history);
        AppLog.Info($"{history.Count} {label} symbol(s) remembered in {store.FileName}.");
        return messages.Count;
    }

    /// <summary>
    /// Looks a group up in the history by every alias it has. Several of them can match (a
    /// legacy entry under an old exchange's name, say) — <see cref="AlertHistoryStore.PreferHigherMilestone"/>
    /// picks the one worth keeping.
    /// </summary>
    private static (string? Key, AlertRecord? Record) FindHistoryEntry(
        IReadOnlyList<string> aliases, Dictionary<string, AlertRecord> history)
    {
        string? bestKey = null;
        AlertRecord bestRecord = default;

        foreach (var alias in aliases)
        {
            if (!history.TryGetValue(alias, out var record))
                continue;

            if (bestKey is null || AlertHistoryStore.PreferHigherMilestone(record, bestRecord).Equals(record))
            {
                bestKey = alias;
                bestRecord = record;
            }
        }

        return bestKey is null ? (null, null) : (bestKey, bestRecord);
    }
}
