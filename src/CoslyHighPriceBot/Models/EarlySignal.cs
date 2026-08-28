namespace CoslyHighPriceBot.Models;

/// <summary>Which candle set off the signal.</summary>
internal enum SignalCandle
{
    /// <summary>The last closed candle. This is the path the thresholds were measured on.</summary>
    Closed,

    /// <summary>
    /// The candle still forming. Cuts the delay from one full candle down to one scan
    /// interval, which is the whole point of the module, but it is the less proven path.
    /// </summary>
    Forming
}

/// <summary>
/// A symbol that met every early-pump condition at once, with the numbers that made it
/// qualify so the Telegram message can explain why it fired instead of just naming a coin.
/// </summary>
/// <param name="CandleChangePercent">Open-to-close move of the triggering candle, in %.</param>
/// <param name="VolumeRatio">Triggering candle's volume divided by the average of the preceding ones.</param>
/// <param name="SqueezeRank">Where the pre-breakout band width sat inside its lookback, 0-100. Lower = tighter squeeze.</param>
internal sealed record EarlySignal(
    string Symbol,
    string QuoteAsset,
    decimal Price,
    decimal CandleChangePercent,
    decimal VolumeRatio,
    decimal Rsi,
    decimal SqueezeRank,
    decimal DayChangePercent,
    decimal DayQuoteVolume,
    SignalCandle TriggeredOn);
