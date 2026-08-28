# COSLY High Price Bot

A .NET 9 console app that watches **Binance USD-M futures** for pumps and alerts on
Telegram. It runs **two independent detectors** over the same downloaded data:

| Detector | Question | Default | Message | State file |
| --- | --- | --- | --- | --- |
| **24h pump** — crypto | What already moved a lot today? | +100% in 24h | 🚀 Crypto Pumps | `notified-symbols.json` |
| **24h pump** — tokenized stocks | Same, on a scale equities actually reach | +20% in 24h | 📈 Tokenized Stocks | `notified-stocks.json` |
| **Early pump** — crypto only | What looks like it's starting *right now*? | Bollinger squeeze breakout + 3× volume + RSI 60-85 on 5m candles | ⚡ Early Pump Signal | `notified-early.json` |

The two are wired to separate Telegram channels and fail independently: whichever breaks,
the other still alerts.

If nothing crosses its threshold, **no message is sent** — the run just logs that
nothing matched and exits cleanly. A symbol is announced once and then held quiet: while
it stays above its threshold, and for a configurable cooldown (8h for 24h pumps, 2h for
early signals). That cooldown is what stops a coin that dips and re-crosses the threshold
minutes later from being announced two or three times.

No server, no database. A run scans in a loop for ~13 minutes and exits, so it fits a
GitHub Actions cron or a Windows Task Scheduler job just as well as it did when it was a
one-shot process — which it still is with `Scan:IntervalSeconds = 0`.

## Features

- Polls Binance's public futures 24h ticker and filters by quote asset and minimum % gain
- Separates tokenized equities (`TSLAUSDT`, `MRNAUSDT`, `HOODUSDT`) from crypto using the
  exchange's own `contractType`, and applies a much lower threshold to them, since a +15%
  day for a stock is exceptional
- Skips symbols with suspended trading (`BREAK`/`HALT`), which would otherwise show
  up as huge, untradeable "pumps"
- Sends a single alert per symbol — no repeats while it stays above its threshold,
  plus a cooldown (8h by default) so a dip-and-recross doesn't re-announce it
- Catches moves **as they start**, on 5-minute candles: a Bollinger squeeze breaking
  upwards on a volume spike with RSI confirming — measured at ~12 alerts a day, against
  93 for the volume spike on its own
- Cuts the symbol universe by 24h volume before requesting any candles, so the whole
  scan costs ~385 of Binance's 2400/minute weight budget
- Daily rotating log file with automatic retention cleanup
- Everything configurable via `appsettings.json` or environment variables (for secrets)
- Ready-to-use GitHub Actions workflow to run every 15 minutes for free

## Quick start

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

1. Copy `src/CoslyHighPriceBot/appsettings.ci.json` to
   `src/CoslyHighPriceBot/appsettings.json` and fill in your Telegram bot token and chat ID.
2. Run it:

   ```bash
   dotnet run --project src/CoslyHighPriceBot
   ```

It scans every 60 seconds for ~13 minutes, alerts on anything worth alerting about, and
exits. Add `Scan__IntervalSeconds=0` for a single pass. Exit code `0` on success (whether
or not anything matched), `1` on error.

### Getting a Telegram bot token and chat ID

1. Talk to [@BotFather](https://t.me/BotFather) on Telegram, run `/newbot`, and copy
   the token it gives you.
2. For a private chat: send your bot any message, then open
   `https://api.telegram.org/bot<TOKEN>/getUpdates` and read `message.chat.id`.
3. For a group: add the bot to the group, send `/start@<bot>` there, and read
   `message.chat.id` from the same URL. Group IDs are **negative**.

## How tokenized stocks are detected

Binance futures tags every symbol with a contract type, and that single field does the
whole job:

```
"contractType": "TRADIFI_PERPETUAL"   -> tokenized stock   (~175 symbols)
"contractType": "PERPETUAL"           -> crypto            (~698 symbols)
```

No catalog to maintain and no name-pattern guessing: a newly listed equity is classified
correctly the moment it appears.

This is the main reason the bot reads futures instead of spot. Spot exposes no such
field — there, the only hint is a `B` suffix on the base asset (`AAPLB`), which also
matches BNB, SHIB and ARB. Spot also simply doesn't list many of the symbols worth
watching: `BTRUSDT` pumped 250% as a futures-only listing.

## How the early-pump detector works

A +100% threshold is retrospective by construction: by the time it trips, the move is
over. Measured against the live API, **0 of 524** crypto perpetuals were above +100% and
only 7 above +20% — the alert is silent almost always, and late when it speaks.

So the second detector looks at the shape a move makes as it begins, on 5-minute candles,
requiring all of these on the same candle:

1. **Bollinger breakout** — the close crosses above the upper band (a crossing, not merely
   being above: riding the band means the move already happened)
2. **A squeeze first** — band width in the tightest 20% of the last 96 candles (8 hours)
3. **Volume spike** — at least 3× the average of the previous 20 candles
4. **RSI 60-85** — a floor to confirm the move, a ceiling to skip ones already spent
5. **A real candle** — at least 1.5% from open to close

Those numbers were measured rather than guessed, over 166 symbols × 1000 five-minute
candles (~3.5 days):

| Configuration | Alerts/day |
| --- | --- |
| Volume spike alone | 93 |
| + Bollinger breakout + RSI | 53 |
| **+ squeeze (the defaults)** | **12.4** |
| Volume ≥5×, body ≥2.5% | 2.6 |

The squeeze is what makes it usable — it cuts the noise fourfold without losing the tail.
Over that sample, the median best move was +1.7% at 1h and +3.1% at 4h, 12% reached +10%
and 2% reached +30% within 4h, against a median drawdown of -1.5%.

> Treat this as a **candidate finder, not a proven edge**. 43 triggers over 3.5 days is a
> small sample, and the median sits close to the drawdown. Whatever value is there lives
> in the tail. Tune the thresholds on your own data before relying on them.

**Cost control.** Candles are one call per symbol, so the universe is cut first using only
the ticker already in memory: crypto perpetuals in `TRADING` status above
`Scan:MinQuoteVolume24h`, minus anything in cooldown, capped at `Scan:MaxSymbols`. At the
5,000,000 USDT default that's ~172 of 703 USDT pairs, consuming ~385 weight per scan out
of 2400/minute. Binance publishes no market cap, so 24h volume stands in for it.

## Configuration

Every adjustable value lives in `appsettings.json`:

| Key | Description |
| --- | --- |
| `Binance:Ticker24hUrl` | Futures 24h ticker. With no query string it returns every symbol. |
| `Binance:ExchangeInfoUrl` | Symbol status and `contractType` (which classifies each symbol). |
| `Binance:KlinesUrl` | Candles, one call per symbol. Early detector only. |
| `Binance:QuoteAsset` | Quote asset to filter by (symbol suffix), e.g. `USDT`. |
| `Binance:OnlyTradingSymbols` | Discards suspended pairs (recommended: `true`). |
| `Filter:MinChangePercent` | Minimum 24h gain, in %, for crypto. |
| `Filter:StockMinChangePercent` | Minimum 24h gain, in %, for tokenized stocks. |
| `Filter:CooldownHours` | Hours before the same symbol can be alerted again. `0` disables it. |
| `State:NotifiedSymbolsFile` | Tracks already-notified crypto. |
| `State:NotifiedStocksFile` | Tracks already-notified tokenized stocks. |
| `State:NotifiedEarlyFile` | Tracks already-notified early signals. |
| `Logging:RetentionDays` | Days of logs to keep. `0` = keep them all. |
| `Telegram:ApiBaseUrl` | Bot API base URL. |
| `Telegram:BotToken` | Your bot's token. **Secret — never commit this.** |
| `Telegram:ChatIds` | Comma-separated destination chats. Private = positive ID; group/channel = **negative**. |
| `Telegram:EarlyChatIds` | Destination chats for early-pump alerts. **Empty = same as `ChatIds`.** |

Everything under `Scan:` belongs to the early detector — the ones worth knowing:

| Key | Default | Description |
| --- | --- | --- |
| `Scan:Enabled` | `true` | `false` leaves only the 24h alerts. |
| `Scan:IntervalSeconds` | `60` | Seconds between scans. `0` = one scan and exit. |
| `Scan:MaxRunMinutes` | `13` | How long a run keeps scanning. |
| `Scan:MinQuoteVolume24h` | `5000000` | Liquidity floor. The pre-filter that bounds the call count. |
| `Scan:VolumeSpikeMultiplier` | `3` | Times the recent average the candle's volume must be. |
| `Scan:SqueezePercentile` | `0.20` | How tight the bands must have been. `1` disables it. |
| `Scan:RsiMin` / `Scan:RsiMax` | `60` / `85` | The RSI band the trigger has to sit in. |
| `Scan:MinCandleBodyPercent` | `1.5` | Minimum open-to-close move of the candle. |
| `Scan:CooldownHours` | `2` | Hours before the same symbol can fire again. |

The rest (`KlineInterval`, `KlineLimit`, `MaxSymbols`, `MaxConcurrentRequests`,
`BollingerPeriod`, `BollingerStdDev`, `SqueezeLookback`, `VolumeAvgPeriod`, `RsiPeriod`,
`MaxQuoteVolume24h`, `EvaluateFormingCandle`) is documented in `AppSettings.cs`.

Any key can be overridden with an environment variable using a double underscore as
the separator, e.g. `Telegram__BotToken`, `Scan__VolumeSpikeMultiplier`. This is how
secrets are passed in the cloud without writing them to a file.

`appsettings.json` (with your real token) is gitignored. `appsettings.ci.json` is a
credential-free template you can copy from.

## Deploying to GitHub Actions (free)

The included [`.github/workflows/pump-alert.yml`](.github/workflows/pump-alert.yml)
runs the bot every 15 minutes without needing any machine to stay on.

1. Fork or push this repo to GitHub — **keep it public** so Actions minutes are free
   (2,880 runs/month exceeds the private-repo free tier).
2. Add three repository secrets (Settings → Secrets and variables → Actions):
   `TELEGRAM_BOT_TOKEN`, `TELEGRAM_CHAT_IDS` and `TELEGRAM_EARLY_CHAT_IDS`. Leave the
   last one unset to send early-pump alerts to the same channel as the rest.
3. Trigger it once manually from the Actions tab to confirm it works, or just wait
   for the next scheduled run.

The bot's memory (the three files under `state/`) is committed back to the repo after each
run, since GitHub Actions runners are ephemeral and don't persist disk state between runs
on their own. Each run's log is also uploaded as a downloadable artifact (90-day
retention), particularly useful when a run fails.

> **Keeping the repo public matters more now.** Each run scans for ~13 minutes instead of
> exiting immediately, so it bills ~14 minutes rather than 1 — roughly 40,000 minutes a
> month. That's free on a public repo and expensive on a private one; set
> `Scan:MaxRunMinutes` to `0` there to go back to one scan per run.

> **Why `www.binance.com/fapi/...` and not `fapi.binance.com`?** Binance blocks US
> datacenter IPs, which is where GitHub Actions runners live. Measured from a runner:
> `fapi.binance.com` → **451**, `api.binance.com` → 451, `fapi1/2/3.binance.com` → 202
> with an empty body, but **`www.binance.com/fapi/v1/...` → 200** and serves the full
> futures API. That host is what makes free CI hosting workable.

## Deploying with Windows Task Scheduler

```bash
publish.cmd
```

This produces a single-file `publish\CoslyHighPriceBot.exe` (~575 KB, framework-dependent
— needs the .NET 9 runtime installed). Point a Task Scheduler action at that `.exe` on
whatever interval you like; the working directory doesn't matter, since configuration
is always read from the executable's own folder.

## Project structure

```
src/CoslyHighPriceBot/
├─ Program.cs                    bootstrap + scan loop; runs both detectors off one ticker
├─ Configuration/AppSettings.cs  appsettings.json POCOs + validation
├─ Modules/
│  ├─ DailyPumpModule.cs         the 24h detector: crypto and tokenized stocks
│  └─ EarlyPumpModule.cs         pre-filter → candles → indicators → alert
├─ Models/
│  ├─ Ticker24h.cs               Binance DTOs (all strings) + CoinKind and Coin
│  ├─ Kline.cs                   one candle; klines are arrays, so it maps by position
│  └─ EarlySignal.cs             a symbol that met every condition, with the numbers
└─ Services/
   ├─ BinanceClient.cs           24h ticker, symbol metadata, candles, 429/418 backoff
   ├─ SymbolMetadataCache.cs     fetches exchangeInfo once per run and shares it
   ├─ Indicators.cs              Bollinger, Wilder RSI, SMA, percentile — pure, no I/O
   ├─ CoinFilter.cs              candidates, then classify by contractType + per-kind threshold
   ├─ MessageFormatter.cs        HTML message text, split if it exceeds 4096 chars
   ├─ TelegramNotifier.cs        POST to sendMessage, per-channel routing
   ├─ AlertHistoryStore.cs       reads/writes symbol -> last-alerted timestamp
   └─ AppLog.cs                  console + daily file in Logs/
```

No dependency injection or generic host — two modules constructed by hand don't need
either. `global.json` pins the SDK version.

## Notes on Binance's API

- Every numeric field in Binance's responses comes back as a string; parsing uses
  `CultureInfo.InvariantCulture` throughout.
- Symbols not in `TRADING` status keep their 24h stats frozen, so they can look like
  enormous pumps that are actually untradeable — that's what `OnlyTradingSymbols`
  guards against.
- Futures `exchangeInfo` accepts no `symbols` filter and always returns the full ~1 MB
  catalog, so it's fetched once per run and cached. With the early detector off, a quiet
  run still costs exactly one Binance call.
- **Klines are the only per-symbol call.** They come back as an array of arrays of mixed
  types rather than objects, so they're mapped by position. Weight is 1 up to 100 candles
  and 2 up to 500, which is why the default limit is 150. 429/418 responses are retried
  with backoff and then abandon that one scan, leaving the rest of the run alone.

## License

No license file yet — all rights reserved by default until one is added. If you'd
like to use or fork this project, consider opening an issue to ask about adding an
open-source license (MIT is a common, permissive choice).
