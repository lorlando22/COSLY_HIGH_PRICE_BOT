namespace CoslyHighPriceBot.Models;

/// <summary>
/// What kind of asset a symbol represents. Each kind has its own threshold, its own
/// Telegram message and its own already-notified file.
/// </summary>
internal enum CoinKind
{
    Crypto,
    TokenizedStock
}

/// <summary>
/// What an exchange's instrument catalog says about one symbol, already reduced to what the
/// rest of the bot needs. Every exchange client builds one of these per symbol from its own
/// catalog response (exchangeInfo, instruments-info, contracts...), so a untradeable or
/// unclassifiable symbol is discarded the same way regardless of which exchange it came from.
/// </summary>
/// <param name="Tradeable">
/// False for anything that shouldn't generate an alert: suspended, not USDT-settled, a
/// dated future instead of a perpetual, still pre-listing, or a catalog tier the bot
/// doesn't recognize yet. <see cref="Kind"/> and <see cref="Aliases"/> are meaningless when
/// this is false.
/// </param>
/// <param name="Kind">Null when <see cref="Tradeable"/> is false.</param>
/// <param name="Aliases">
/// Every name this symbol is known by across exchanges, primary name first — the one used
/// as the group's key when this is the highest-priority quote in it (see
/// <see cref="CoinGroup"/>). Binance and most Bybit coins have exactly one; a BingX crypto
/// coin has two when its internal symbol and its <c>displayName</c> disagree (NEIROCTO vs
/// NEIRO), and a tokenized stock can have two or three (baseCoin, underlyingTicker, and for
/// BingX commodities the ticker inside the parentheses of the display name).
/// </param>
/// <param name="Multiplier">
/// The "large supply" factor folded into a crypto symbol's name (1000PEPE = 1000, plain
/// PEPE = 1). Always 1 for a tokenized stock, which never carries one. Used only to adjust
/// the price used for cross-exchange price-compatibility checks — the 24h percentage
/// doesn't depend on it.
/// </param>
/// <param name="DiscardReason">Null when <see cref="Tradeable"/> is true; logged otherwise.</param>
internal sealed record InstrumentInfo(
    bool Tradeable,
    CoinKind? Kind,
    IReadOnlyList<string> Aliases,
    decimal Multiplier,
    string? DiscardReason)
{
    /// <summary>
    /// How the exchange's own app lists the symbol, when that differs from the API symbol —
    /// BingX shows NCSKTSLA2USD-USDT as "TSLA-USDT" and MONAD-USDT as "MON-USDT". Null means
    /// the API symbol is already what a trader searches for. Display only.
    /// </summary>
    public string? DisplaySymbol { get; init; }
}
