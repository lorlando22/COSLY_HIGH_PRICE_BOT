using System.Text;
using CoslyHighPriceBot.Models;

namespace CoslyHighPriceBot.Services;

/// <summary>Arma el texto del aviso de Telegram usando parse_mode HTML.</summary>
internal static class MessageFormatter
{
    /// <summary>
    /// Telegram corta los mensajes en 4096 caracteres. Dejamos margen para el encabezado
    /// y para que ningún bloque de moneda quede partido a la mitad.
    /// </summary>
    private const int MaxBodyLength = 3600;

    public static IReadOnlyList<string> Build(IReadOnlyList<Coin> coins, string quoteAsset, decimal minChangePercent)
    {
        var bodies = Chunk(coins.Select((coin, index) => BuildBlock(coin, index + 1)));

        return bodies
            .Select((body, index) =>
            {
                var part = bodies.Count > 1 ? $" · parte {index + 1}/{bodies.Count}" : "";
                return BuildHeader(quoteAsset, minChangePercent, part) + "\n\n" + body;
            })
            .ToList();
    }

    private static string BuildHeader(string quoteAsset, decimal minChangePercent, string part) =>
        $"🚀 <b>Pumps {Escape(quoteAsset)} — últimas 24h</b>\n" +
        $"<i>{DateTime.UtcNow:dd/MM/yyyy HH:mm} UTC · umbral +{minChangePercent:0.##}%{part}</i>";

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
            .Append($"💵 Precio: {FormatPrice(coin.LastPrice)}\n")
            .Append($"📊 Apertura: {FormatPrice(coin.OpenPrice)}\n")
            .Append($"🔺 Máx: {FormatPrice(coin.HighPrice)}   🔻 Mín: {FormatPrice(coin.LowPrice)}\n")
            .Append($"💰 Volumen 24h: {FormatVolume(coin.QuoteVolume)} {Escape(coin.QuoteAsset)}\n")
            .Append($"🔁 Operaciones: {coin.TradeCount:N0}\n\n")
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

    /// <summary>Los precios van desde decenas de miles hasta 0.00000001, así que el formato se adapta.</summary>
    private static string FormatPrice(decimal value) =>
        value >= 1m ? value.ToString("N4") : value.ToString("0.########");

    private static string FormatVolume(decimal value) => value switch
    {
        >= 1_000_000_000m => (value / 1_000_000_000m).ToString("0.##") + "B",
        >= 1_000_000m => (value / 1_000_000m).ToString("0.##") + "M",
        >= 1_000m => (value / 1_000m).ToString("0.##") + "K",
        _ => value.ToString("0.##")
    };

    /// <summary>Escapado obligatorio para parse_mode HTML.</summary>
    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
