using CoslyHighPriceBot.Configuration;
using CoslyHighPriceBot.Models;
using CoslyHighPriceBot.Services;

namespace CoslyHighPriceBot.Modules;

/// <summary>
/// Tries to catch a pump as it starts instead of reporting one that already finished.
/// <para>
/// It reads intraday candles and looks for the shape a move makes when it begins: Bollinger
/// bands that had gone quiet suddenly broken upwards, on a candle whose volume dwarfs the
/// recent average, with RSI high enough to confirm but not so high the move is spent. All
/// conditions have to hold at once — measured over 166 symbols and about 3.5 days of
/// five-minute candles, that is roughly 12 alerts a day, against 93 for the volume spike
/// alone.
/// </para>
/// Crypto only. Tokenized stocks are filtered out by contract type: they don't behave this
/// way and they already have their own alert.
/// </summary>
internal sealed class EarlyPumpModule(
    BinanceClient binance,
    TelegramNotifier telegram,
    SymbolMetadataCache metadataCache,
    AppSettings settings,
    AlertHistoryStore store)
{
    /// <summary>Contract type of a plain crypto perpetual. Anything else is a quarterly or a tokenized stock.</summary>
    private const string CryptoContractType = "PERPETUAL";

    /// <summary>Weight consumed in a minute past which the log starts warning. Binance's cap is 2400.</summary>
    private const int WeightWarningLevel = 1200;

    private readonly Dictionary<string, DateTimeOffset> history = new(store.Load(), StringComparer.Ordinal);

    private bool firstScan = true;

    public void LogState() =>
        AppLog.Info($"Early-pump module enabled ({settings.Scan.KlineInterval} candles, " +
                    $"volume >= {settings.Scan.VolumeSpikeMultiplier:0.##}x, RSI {settings.Scan.RsiMin:0}-{settings.Scan.RsiMax:0}, " +
                    $"squeeze p{settings.Scan.SqueezePercentile * 100:0}). {history.Count} symbol(s) in cooldown ({store.FileName}).");

    /// <summary>Returns how many Telegram messages went out.</summary>
    public async Task<int> RunAsync(IReadOnlyList<Ticker24h> tickers, CancellationToken cancellationToken)
    {
        var options = settings.Scan;
        var now = DateTimeOffset.UtcNow;

        // Unlike the 24h module, an early signal is a moment rather than a state that lasts
        // all day, so there's nothing to stay "still true": the cooldown alone decides.
        var changed = DropExpired(now, TimeSpan.FromHours(options.CooldownHours));

        var metadata = await metadataCache.GetAsync(cancellationToken);
        var universe = SelectUniverse(tickers, metadata);

        if (universe.Count == 0)
        {
            if (changed)
                store.Save(history);

            firstScan = false;
            return 0;
        }

        List<(Ticker24h Ticker, IReadOnlyList<Kline> Klines)> candles;
        try
        {
            candles = await FetchCandlesAsync(universe, cancellationToken);
        }
        catch (BinanceRateLimitException ex)
        {
            // Being throttled isn't a failure of the run: the next scan is a minute away and
            // the 24h module has already done its work with the ticker it shares.
            AppLog.Warn($"Early-pump scan abandoned, Binance is throttling: {ex.Message}");

            if (changed)
                store.Save(history);

            firstScan = false;
            return 0;
        }

        var signals = new List<EarlySignal>();
        foreach (var (ticker, klines) in candles)
        {
            if (Evaluate(ticker, klines, now) is { } signal)
                signals.Add(signal);
        }

        if (firstScan || binance.UsedWeightLastMinute >= WeightWarningLevel)
            LogWeight(universe.Count);

        firstScan = false;

        if (signals.Count == 0)
        {
            if (changed)
                store.Save(history);

            return 0;
        }

        signals = signals.OrderByDescending(s => s.VolumeRatio).ToList();

        AppLog.Info($"{signals.Count} early pump signal(s):");
        foreach (var signal in signals)
            AppLog.Info($"  {signal.Symbol,-16} candle {signal.CandleChangePercent,7:+0.00;-0.00}%  " +
                        $"vol {signal.VolumeRatio,5:0.0}x  RSI {signal.Rsi,5:0.0}  " +
                        $"squeeze p{signal.SqueezeRank,-3:0}  ({signal.TriggeredOn.ToString().ToLowerInvariant()} candle)");

        var messages = MessageFormatter.BuildEarly(signals, options.KlineInterval, options.SqueezeLookback);
        foreach (var message in messages)
            await telegram.SendAsync(message, settings.Telegram.GetEarlyChatIds(), cancellationToken);

        foreach (var signal in signals)
            history[signal.Symbol] = now;

        // Saved only after a successful send, so a Telegram failure leaves nothing marked
        // as notified and the next scan retries.
        store.Save(history);
        AppLog.Info($"{history.Count} symbol(s) in early-pump cooldown ({store.FileName}).");

        return messages.Count;
    }

    /// <summary>Returns whether anything was actually removed, so an unchanged file isn't rewritten every minute.</summary>
    private bool DropExpired(DateTimeOffset now, TimeSpan cooldown)
    {
        var expired = history.Where(entry => now - entry.Value >= cooldown).Select(entry => entry.Key).ToList();

        foreach (var symbol in expired)
        {
            history.Remove(symbol);
            AppLog.Info($"{symbol}'s early-pump cooldown expired: removed from {store.FileName}.");
        }

        return expired.Count > 0;
    }

    /// <summary>
    /// The pre-filter, and the reason this module is affordable at all. Candles cost one
    /// call per symbol, so the set has to be cut down before any of them are requested.
    /// Everything here comes out of the ticker that was already downloaded — no extra calls.
    /// </summary>
    private List<Ticker24h> SelectUniverse(
        IReadOnlyList<Ticker24h> tickers, IReadOnlyDictionary<string, SymbolInfo> metadata)
    {
        var options = settings.Scan;
        var quoteAsset = settings.Binance.QuoteAsset;

        var stocks = 0;
        var illiquid = 0;
        var cooling = 0;

        var universe = new List<(Ticker24h Ticker, decimal Volume)>();

        foreach (var ticker in tickers)
        {
            // Quarterly contracts look like BTCUSDT_250926, so they don't end in the quote
            // asset and are skipped here without needing a special case.
            if (ticker.Symbol.Length <= quoteAsset.Length ||
                !ticker.Symbol.EndsWith(quoteAsset, StringComparison.Ordinal))
                continue;

            if (!metadata.TryGetValue(ticker.Symbol, out var info))
                continue;

            if (!string.Equals(info.ContractType, CryptoContractType, StringComparison.Ordinal))
            {
                stocks++;
                continue;
            }

            // Suspended pairs keep their stats frozen and look like pumps that can't be traded.
            if (settings.Binance.OnlyTradingSymbols && !string.Equals(info.Status, "TRADING", StringComparison.Ordinal))
                continue;

            if (history.ContainsKey(ticker.Symbol))
            {
                cooling++;
                continue;
            }

            var volume = CoinFilter.Parse(ticker.QuoteVolume);
            if (volume < options.MinQuoteVolume24h ||
                (options.MaxQuoteVolume24h > 0 && volume > options.MaxQuoteVolume24h))
            {
                illiquid++;
                continue;
            }

            universe.Add((ticker, volume));
        }

        // Most liquid first, so the cap cuts the least interesting tail rather than an arbitrary slice.
        var selected = universe
            .OrderByDescending(entry => entry.Volume)
            .Take(options.MaxSymbols)
            .Select(entry => entry.Ticker)
            .ToList();

        if (firstScan)
            AppLog.Info($"Early-pump universe: {selected.Count} symbol(s) to scan " +
                        $"({stocks} tokenized stock(s), {illiquid} below {options.MinQuoteVolume24h:N0} {quoteAsset} " +
                        $"and {cooling} in cooldown left out).");

        return selected;
    }

    /// <summary>
    /// Downloads candles for the whole universe, a bounded number of requests at a time.
    /// A symbol that fails is dropped with a warning: one bad response shouldn't cost the
    /// scan. A rate limit is different — it means every other request is doomed too, so it
    /// stops the scan and lets the run continue with the next one a minute later.
    /// </summary>
    private async Task<List<(Ticker24h Ticker, IReadOnlyList<Kline> Klines)>> FetchCandlesAsync(
        List<Ticker24h> universe, CancellationToken cancellationToken)
    {
        var options = settings.Scan;
        using var slots = new SemaphoreSlim(options.MaxConcurrentRequests);

        var tasks = universe.Select(async ticker =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                var klines = await binance.GetKlinesAsync(
                    ticker.Symbol, options.KlineInterval, options.KlineLimit, cancellationToken);

                return (Ticker: ticker, Klines: (IReadOnlyList<Kline>?)klines);
            }
            catch (InvalidOperationException ex)
            {
                AppLog.Warn($"Could not read {ticker.Symbol} candles: {ex.Message}");
                return (Ticker: ticker, Klines: null);
            }
            finally
            {
                slots.Release();
            }
        });

        var results = await Task.WhenAll(tasks);

        return results
            .Where(result => result.Klines is not null)
            .Select(result => (result.Ticker, result.Klines!))
            .ToList();
    }

    /// <summary>
    /// Runs the conditions over one symbol's candles. Indicators are always computed on
    /// closed candles; the candle still forming is only ever the one being tested.
    /// </summary>
    private EarlySignal? Evaluate(Ticker24h ticker, IReadOnlyList<Kline> klines, DateTimeOffset now)
    {
        var options = settings.Scan;

        // Binance includes the candle in progress, recognisable by a close time still ahead.
        var lastIsForming = klines.Count > 0 && klines[^1].CloseTime > now;
        var closedCount = lastIsForming ? klines.Count - 1 : klines.Count;

        // Enough history for the band window plus the whole squeeze lookback, or the symbol
        // is too newly listed to say anything about.
        if (closedCount < options.SqueezeLookback + options.BollingerPeriod + 2)
            return null;

        var closes = new double[closedCount];
        var volumes = new double[closedCount];
        for (var i = 0; i < closedCount; i++)
        {
            closes[i] = (double)klines[i].Close;
            volumes[i] = (double)klines[i].BaseVolume;
        }

        var bands = Indicators.Bollinger(closes, options.BollingerPeriod, (double)options.BollingerStdDev);
        var last = closedCount - 1;

        if (bands[last] is not { } band || bands[last - 1] is not { } previousBand)
            return null;

        var closedSignal = EvaluateClosed(ticker, klines[last], closes, volumes, bands, last, band, previousBand);
        if (closedSignal is not null)
            return closedSignal;

        if (!options.EvaluateFormingCandle || !lastIsForming)
            return null;

        return EvaluateForming(ticker, klines[^1], closes, volumes, bands, last, band);
    }

    /// <summary>The measured path: the last closed candle broke out of the bands.</summary>
    private EarlySignal? EvaluateClosed(
        Ticker24h ticker,
        Kline candle,
        double[] closes,
        double[] volumes,
        Indicators.BollingerPoint?[] bands,
        int last,
        Indicators.BollingerPoint band,
        Indicators.BollingerPoint previousBand)
    {
        var options = settings.Scan;

        // A crossing, not merely "above": a symbol that has been riding the upper band for
        // an hour isn't starting a move, it's already in the middle of one.
        if (closes[last] <= band.Upper || closes[last - 1] > previousBand.Upper)
            return null;

        if (candle.BodyPercent < options.MinCandleBodyPercent)
            return null;

        // The candle is excluded from the average it's being compared against.
        if (Indicators.Sma(volumes, options.VolumeAvgPeriod, last - 1) is not { } baseline || baseline <= 0d)
            return null;

        var volumeRatio = (decimal)(volumes[last] / baseline);
        if (volumeRatio < options.VolumeSpikeMultiplier)
            return null;

        if (Rsi(closes, closes.Length - 1) is not { } rsi || rsi < options.RsiMin || rsi > options.RsiMax)
            return null;

        if (Squeeze(bands, last) is not { } squeezeRank)
            return null;

        return Build(ticker, candle.Close, candle.BodyPercent, volumeRatio, rsi, squeezeRank, SignalCandle.Closed);
    }

    /// <summary>
    /// The low-latency path: the candle in progress has already done enough to qualify.
    /// Its partial volume is compared against the average of whole candles, which can only
    /// understate a spike — so this finds the same moves sooner without inventing new ones.
    /// It is not covered by the backtest, and Scan:EvaluateFormingCandle turns it off.
    /// </summary>
    private EarlySignal? EvaluateForming(
        Ticker24h ticker,
        Kline candle,
        double[] closes,
        double[] volumes,
        Indicators.BollingerPoint?[] bands,
        int last,
        Indicators.BollingerPoint band)
    {
        var options = settings.Scan;

        // Compared against the bands as they stood at the last close: the break has to be
        // what the forming candle did, not a level it inherited.
        if ((double)candle.Close <= band.Upper || closes[last] > band.Upper)
            return null;

        if (candle.BodyPercent < options.MinCandleBodyPercent)
            return null;

        if (Indicators.Sma(volumes, options.VolumeAvgPeriod, last) is not { } baseline || baseline <= 0d)
            return null;

        var volumeRatio = (decimal)((double)candle.BaseVolume / baseline);
        if (volumeRatio < options.VolumeSpikeMultiplier)
            return null;

        // RSI has to account for the move under way, so the forming close joins the series.
        var extended = new double[closes.Length + 1];
        closes.CopyTo(extended, 0);
        extended[^1] = (double)candle.Close;

        if (Rsi(extended, extended.Length - 1) is not { } rsi || rsi < options.RsiMin || rsi > options.RsiMax)
            return null;

        if (Squeeze(bands, last + 1) is not { } squeezeRank)
            return null;

        return Build(ticker, candle.Close, candle.BodyPercent, volumeRatio, rsi, squeezeRank, SignalCandle.Forming);
    }

    private decimal? Rsi(IReadOnlyList<double> closes, int index)
    {
        var series = Indicators.Rsi(closes, settings.Scan.RsiPeriod);
        return series[index] is { } value ? (decimal)value : null;
    }

    /// <summary>
    /// Were the bands tight enough just before the break? The window is the
    /// <paramref name="endExclusive"/> candles before the one being tested, and the tested
    /// width has to sit inside its tightest slice. Returns where it sat, as 0-100, or null
    /// when the bands were not compressed at all.
    /// </summary>
    private decimal? Squeeze(Indicators.BollingerPoint?[] bands, int endExclusive)
    {
        var options = settings.Scan;

        var window = new List<double>(options.SqueezeLookback);
        for (var i = Math.Max(0, endExclusive - options.SqueezeLookback); i < endExclusive; i++)
        {
            if (bands[i] is { } point)
                window.Add(point.Width);
        }

        if (window.Count == 0 || bands[endExclusive - 1] is not { } tested)
            return null;

        if (tested.Width > Indicators.Percentile(window, (double)options.SqueezePercentile))
            return null;

        return (decimal)Indicators.Rank(window, tested.Width);
    }

    private EarlySignal Build(
        Ticker24h ticker,
        decimal price,
        decimal candleChangePercent,
        decimal volumeRatio,
        decimal rsi,
        decimal squeezeRank,
        SignalCandle triggeredOn) =>
        new(
            Symbol: ticker.Symbol,
            QuoteAsset: settings.Binance.QuoteAsset,
            Price: price,
            CandleChangePercent: candleChangePercent,
            VolumeRatio: volumeRatio,
            Rsi: rsi,
            SqueezeRank: squeezeRank,
            DayChangePercent: CoinFilter.Parse(ticker.PriceChangePercent),
            DayQuoteVolume: CoinFilter.Parse(ticker.QuoteVolume),
            TriggeredOn: triggeredOn);

    private void LogWeight(int symbolCount)
    {
        if (binance.UsedWeightLastMinute is not { } weight)
            return;

        var message = $"Binance weight used in the last minute: {weight} (cap 2400) after {symbolCount} candle call(s).";

        if (weight >= WeightWarningLevel)
            AppLog.Warn(message + " Consider raising Scan:MinQuoteVolume24h or Scan:IntervalSeconds.");
        else
            AppLog.Info(message);
    }
}
