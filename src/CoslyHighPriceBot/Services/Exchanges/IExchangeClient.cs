using CoslyHighPriceBot.Models;

namespace CoslyHighPriceBot.Services.Exchanges;

/// <summary>
/// One exchange's 24h ticker and instrument catalog, both already turned into this bot's own
/// shapes. Binance, Bybit and BingX each implement this the same way Binance always worked
/// on its own, so Program.cs and <see cref="PumpAggregator"/> never need an exchange-specific
/// branch.
/// </summary>
internal interface IExchangeClient
{
    /// <summary>Used everywhere else: logs, the priority list, and the exchange name shown in a message.</summary>
    string Name { get; }

    /// <summary>
    /// The full 24h ticker, unfiltered — quote-asset and tradeability are both decided from
    /// the instrument catalog instead (see <see cref="GetInstrumentsAsync"/>), since at least
    /// one exchange (Bybit) doesn't expose the settlement asset on the ticker itself.
    /// </summary>
    Task<IReadOnlyList<MarketTicker>> GetTickersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Status, kind and aliases for every symbol, keyed by the same symbol used in
    /// <see cref="GetTickersAsync"/>. Each client filters tradeability itself (Binance folds
    /// its own <c>OnlyTradingSymbols</c> switch in here too), so a caller only ever needs to
    /// check <see cref="InstrumentInfo.Tradeable"/>.
    /// </summary>
    Task<IReadOnlyDictionary<string, InstrumentInfo>> GetInstrumentsAsync(CancellationToken cancellationToken);
}
