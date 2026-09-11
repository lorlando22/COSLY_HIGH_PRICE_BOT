using System.Text;
using CoslyHighPriceBot.Models;

namespace CoslyHighPriceBot.Services;

/// <summary>Builds the Telegram alert text using parse_mode HTML.</summary>
internal static class MessageFormatter
{
    /// <summary>
    /// Telegram truncates messages at 4096 characters. We leave room for the header
    /// and so no coin block gets split in half.
    /// </summary>
    private const int MaxBodyLength = 3600;

    /// <summary>
    /// Crypto and tokenized stocks get their own message, each with its own title and
    /// threshold, so the 4096-character limit applies to each one separately. Groups arrive
    /// paired with the milestone they reached — the threshold itself for a first alert, or a
    /// higher one for crypto that kept climbing (see <see cref="CoinFilter.Milestone"/>).
    /// </summary>
    public static IReadOnlyList<string> Build(
        IReadOnlyList<(CoinGroup Group, decimal Milestone)> groups, decimal minChangePercent, string quoteAsset)
    {
        var bodies = Chunk(groups.Select((entry, index) =>
            BuildBlock(entry.Group, entry.Milestone, minChangePercent, index + 1, quoteAsset)));
        var title = Title(groups[0].Group.Kind);

        return bodies
            .Select((body, index) =>
            {
                var part = bodies.Count > 1 ? $" · part {index + 1}/{bodies.Count}" : "";
                return BuildHeader(title, minChangePercent, part) + "\n\n" + body;
            })
            .ToList();
    }

    private static string Title(CoinKind kind) => kind switch
    {
        CoinKind.TokenizedStock => "📈 <b>Tokenized Stocks — last 24h</b>",
        _ => "🚀 <b>Crypto Pumps — last 24h</b>"
    };

    private static string BuildHeader(string title, decimal minChangePercent, string part) =>
        $"{title}\n" +
        $"<i>{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC · threshold +{minChangePercent:0.##}%{part}</i>";

    private static string BuildBlock(CoinGroup group, decimal milestone, decimal threshold, int position, string quoteAsset)
    {
        // Both are guaranteed non-null: a group only reaches here because at least one of
        // its quotes cleared the threshold (see DailyPumpModule).
        var bestChange = group.BestQualifyingChangePercent(threshold)!.Value;
        var bestQuote = group.BestQualifyingQuote(threshold)!;

        var block = new StringBuilder()
            .Append($"<b>{position}. {Escape(group.Key)}</b> — <b>{FormatPercent(bestChange)}</b> (24h)\n");

        // Only a milestone above the base threshold is worth a line — at the threshold
        // itself the block reads exactly as it did before milestones existed.
        if (milestone > threshold)
            block.Append($"🎯 Milestone: +{milestone:0.##}%\n");

        // Every exchange the coin is tradeable on, in priority order, not just the ones that
        // qualify — a coin can be pumping hard on one exchange and barely moving on another,
        // and that's worth showing.
        for (var i = 0; i < group.Quotes.Count; i++)
        {
            var quote = group.Quotes[i];
            var prefix = i == 0 ? "🏦 " : "   ";
            var check = quote.ChangePercent >= threshold ? " ✅" : "";
            block.Append($"{prefix}{Escape(quote.ExchangeName)} {Escape(quote.DisplaySymbol ?? quote.Symbol)}: {FormatPercent(quote.ChangePercent)}{check}\n");
        }

        block
            .Append($"💵 Price: {FormatPrice(bestQuote.LastPrice)} ({Escape(bestQuote.ExchangeName)})\n")
            .Append($"📊 Open: {FormatPrice(bestQuote.OpenPrice)}\n")
            .Append($"🔺 High: {FormatPrice(bestQuote.HighPrice)}   🔻 Low: {FormatPrice(bestQuote.LowPrice)}\n")
            .Append($"💰 24h Volume: {FormatVolume(bestQuote.QuoteVolume)} {Escape(quoteAsset)}\n");

        // Only Binance reports a trade count; Bybit and BingX don't, so the line is skipped
        // rather than shown as a misleading zero.
        if (bestQuote.TradeCount is { } trades)
            block.Append($"🔁 Trades: {trades:N0}\n");

        return block.Append('\n').ToString();
    }

    private static string FormatPercent(decimal value) => value.ToString("+0.00;-0.00") + "%";

    private static List<string> Chunk(IEnumerable<string> blocks)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var block in blocks)
        {
            if (current.Length > 0 && current.Length + block.Length > MaxBodyLength)
            {
                chunks.Add(current.ToString().TrimEnd());
                current.Clear();
            }

            current.Append(block);
        }

        if (current.Length > 0)
            chunks.Add(current.ToString().TrimEnd());

        return chunks;
    }

    /// <summary>Prices range from the tens of thousands down to 0.00000001, so the format adapts.</summary>
    private static string FormatPrice(decimal value) =>
        value >= 1m ? value.ToString("N4") : value.ToString("0.########");

    private static string FormatVolume(decimal value) => value switch
    {
        >= 1_000_000_000m => (value / 1_000_000_000m).ToString("0.##") + "B",
        >= 1_000_000m => (value / 1_000_000m).ToString("0.##") + "M",
        >= 1_000m => (value / 1_000m).ToString("0.##") + "K",
        _ => value.ToString("0.##")
    };

    /// <summary>Escaping required for parse_mode HTML. Every dynamic string goes through this — a coin's name can be Chinese (哈基米), which Binance and BingX both use for some display names.</summary>
    internal static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
