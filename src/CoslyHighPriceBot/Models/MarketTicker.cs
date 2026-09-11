namespace CoslyHighPriceBot.Models;

/// <summary>
/// One exchange's 24h snapshot for one symbol, already parsed to decimal. Every exchange
/// client (see <see cref="Services.Exchanges.IExchangeClient"/>) turns its own JSON shape
/// into this one, so the rest of the pipeline — grouping, filtering, formatting — never
/// has to know which exchange a quote came from or what units its raw fields were in
/// (Bybit's 24h change is a fraction, Binance and BingX already send a percent).
/// </summary>
internal sealed record MarketTicker(
    string Symbol,
    decimal ChangePercent,
    decimal LastPrice,
    decimal OpenPrice,
    decimal HighPrice,
    decimal LowPrice,
    decimal QuoteVolume,
    long? TradeCount);
