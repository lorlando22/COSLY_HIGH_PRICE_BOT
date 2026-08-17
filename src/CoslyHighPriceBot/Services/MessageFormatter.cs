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

    public static IReadOnlyList<string> Build(IReadOnlyList<Coin> coins, string quoteAsset, decimal minChangePercent)
    {
        var bodies = Chunk(coins.Select((coin, index) => BuildBlock(coin, index + 1)));

        return bodies
            .Select((body, index) =>
            {
                var part = bodies.Count > 1 ? $" · part {index + 1}/{bodies.Count}" : "";
                return BuildHeader(quoteAsset, minChangePercent, part) + "\n\n" + body;
            })
            .ToList();
    }

    private static string BuildHeader(string quoteAsset, decimal minChangePercent, string part) =>
        $"🚀 <b>{Escape(quoteAsset)} Pumps — last 24h</b>\n" +
        $"<i>{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC · threshold +{minChangePercent:0.##}%{part}</i>";

    private static string BuildBlock(Coin coin, int position)
    {
        var block = new StringBuilder()
            .Append($"<b>{position}. {Escape(coin.Symbol)}</b> — <b>{FormatPercent(coin.ChangePercent)}</b> (24h)\n");

        if (coin.WindowChanges.Count > 0)
        {
            var windows = coin.WindowChanges.Select(w => $"{Escape(w.Window)}: {FormatPercent(w.ChangePercent)}");
            block.Append($"⏱ {string.Join("   ·   ", windows)}\n");
        }

        return block
            .Append($"💵 Price: {FormatPrice(coin.LastPrice)}\n")
            .Append($"📊 Open: {FormatPrice(coin.OpenPrice)}\n")
            .Append($"🔺 High: {FormatPrice(coin.HighPrice)}   🔻 Low: {FormatPrice(coin.LowPrice)}\n")
            .Append($"💰 24h Volume: {FormatVolume(coin.QuoteVolume)} {Escape(coin.QuoteAsset)}\n")
            .Append($"🔁 Trades: {coin.TradeCount:N0}\n\n")
            .ToString();
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

    /// <summary>Escaping required for parse_mode HTML.</summary>
    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
