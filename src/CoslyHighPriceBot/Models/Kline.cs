using System.Globalization;
using System.Text.Json;

namespace CoslyHighPriceBot.Models;

/// <summary>
/// One candle from /fapi/v1/klines. Unlike every other endpoint the bot reads, klines come
/// back as an array of arrays of mixed types instead of objects, so there is nothing for
/// [JsonPropertyName] to bind to and the mapping has to be done by position:
/// <code>[openTime, "open", "high", "low", "close", "volume", closeTime, "quoteVolume", trades, ...]</code>
/// </summary>
internal readonly record struct Kline(
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal BaseVolume,
    DateTimeOffset CloseTime,
    decimal QuoteVolume,
    long TradeCount)
{
    /// <summary>How far the candle travelled between its open and its close, in %.</summary>
    public decimal BodyPercent => Open == 0m ? 0m : (Close - Open) / Open * 100m;

    /// <summary>
    /// Maps one raw row. Returns false instead of throwing when the row is shorter or
    /// differently shaped than expected: a single malformed candle must not abort a scan.
    /// </summary>
    public static bool TryFromArray(JsonElement[] fields, out Kline kline)
    {
        kline = default;

        if (fields.Length < 9)
            return false;

        try
        {
            kline = new Kline(
                OpenTime: DateTimeOffset.FromUnixTimeMilliseconds(fields[0].GetInt64()),
                Open: Number(fields[1]),
                High: Number(fields[2]),
                Low: Number(fields[3]),
                Close: Number(fields[4]),
                BaseVolume: Number(fields[5]),
                CloseTime: DateTimeOffset.FromUnixTimeMilliseconds(fields[6].GetInt64()),
                QuoteVolume: Number(fields[7]),
                TradeCount: fields[8].GetInt64());

            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// Binance sends prices and volumes as strings, but timestamps and counts as numbers,
    /// and it has changed which is which before. Accepting both shapes costs one branch.
    /// </summary>
    private static decimal Number(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetDecimal(),
        JsonValueKind.String => decimal.Parse(
            element.GetString() ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture),
        _ => throw new FormatException($"Unexpected kline field of kind {element.ValueKind}.")
    };
}
