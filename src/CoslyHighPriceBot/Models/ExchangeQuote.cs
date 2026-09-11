namespace CoslyHighPriceBot.Models;

/// <summary>
/// One exchange's contribution to a <see cref="CoinGroup"/>: a tradeable, classified
/// <see cref="MarketTicker"/> joined with what its exchange's catalog said about it.
/// </summary>
/// <param name="AdjustedPrice">
/// <see cref="LastPrice"/> divided by the symbol's multiplier (see
/// <see cref="InstrumentInfo.Multiplier"/>), so a 1000PEPE quote and a plain PEPE quote for
/// the same coin land in the same ballpark. Used only by <see cref="Services.PumpAggregator"/>
/// to decide whether two same-named quotes are really the same coin — never shown to the user.
/// </param>
/// <param name="Aliases">Copied from the symbol's <see cref="InstrumentInfo.Aliases"/>.</param>
internal sealed record ExchangeQuote(
    string ExchangeName,
    string Symbol,
    CoinKind Kind,
    decimal ChangePercent,
    decimal LastPrice,
    decimal OpenPrice,
    decimal HighPrice,
    decimal LowPrice,
    decimal QuoteVolume,
    long? TradeCount,
    decimal AdjustedPrice,
    IReadOnlyList<string> Aliases)
{
    /// <summary>Copied from <see cref="InstrumentInfo.DisplaySymbol"/>; the message falls back to <see cref="Symbol"/>.</summary>
    public string? DisplaySymbol { get; init; }
}
