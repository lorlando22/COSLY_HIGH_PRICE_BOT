# COSLY_HIGH_PRICE_BOT

.NET 9 console app that detects "pumps" on Binance **USD-M futures** and sends formatted
alerts to Telegram. It runs **two independent modules** over the same downloaded data:

| Module | Question it answers | Code |
| --- | --- | --- |
| **24h pump** | What has already moved a lot today? | `Modules/DailyPumpModule.cs` |
| **Early pump** | What looks like it is starting to move right now? | `Modules/EarlyPumpModule.cs` |

They share a ticker download and nothing else: separate thresholds, separate messages,
separate Telegram channels, separate state files, separate cooldowns. Whichever one fails,
the other still alerts.

The 24h module handles **two kinds of asset separately**:

| Kind | Threshold | State file |
| --- | --- | --- |
| Crypto | `Filter:MinChangePercent` (100% default) | `notified-symbols.json` |
| Tokenized stocks | `Filter:StockMinChangePercent` (20% default) | `notified-stocks.json` |

The early module is **crypto only** and keeps its own file, `notified-early.json`.

If nothing exceeds its threshold, **nothing is sent** — it's just logged to the console.

## Tokenized stocks

Binance futures lists tokenized equities, commodities and other traditional-finance
instruments as regular `*USDT` perpetuals (`TSLAUSDT`, `MRNAUSDT`, `HOODUSDT`). They
move far less than crypto — a +15% day is exceptional — so a 100% threshold would never
fire for them, which is why they get their own.

Telling them apart is a single field in `exchangeInfo`:

```
"contractType": "TRADIFI_PERPETUAL"   -> tokenized stock   (~175 symbols)
"contractType": "PERPETUAL"           -> crypto            (~698 symbols)
```

That field is the main reason this bot reads futures rather than spot. **Spot has no
equivalent**: every symbol looks alike there, and the only usable hint is a `B` suffix on
the base asset (`AAPLB`), which misclassifies BNB, SHIB and ARB. An earlier version kept
a hand-maintained catalog to work around that; futures made it unnecessary.

## Early pump detection

The 24h module is **retrospective by construction**: by the time a coin reads +100%, the
move is over. Measured against the live API, 0 of 524 crypto perpetuals were above +100%
and only 7 were above +20% — the module is silent almost always, and late when it speaks.

The early module looks instead at the shape a move makes *as it begins*, on 5-minute
candles. All of these have to be true on the same candle:

| Condition | Default | Why |
| --- | --- | --- |
| Bollinger breakout | close crosses above the upper band | A crossing, not "is above": riding the band means the move already happened |
| Squeeze first | band width in the tightest 20% of the last 96 candles (8h) | The compression that precedes a breakout |
| Volume spike | ≥ 3× the average of the previous 20 candles | The candle is excluded from its own average |
| RSI | between 60 and 85 | A floor to confirm, a ceiling to skip moves already spent |
| Candle body | ≥ 1.5% open to close | "A very strong candle" |

Those defaults were measured, not guessed — 166 symbols × 1000 five-minute candles
(~3.5 days):

| Configuration | Alerts/day |
| --- | --- |
| Volume spike alone | 93 |
| + Bollinger breakout + RSI | 53 |
| **+ squeeze (the defaults)** | **12.4** |
| Volume ≥5×, body ≥2.5% | 2.6 |

**The squeeze is what makes the module usable** — it cuts the noise fourfold without
losing the tail. Over that sample: median best move +1.7% at 1h and +3.1% at 4h, 12%
reached +10% and 2% reached +30% within 4h, against a median drawdown of -1.5%. Treat it
as a candidate finder, not a proven edge: 43 triggers over 3.5 days is a small sample.

### The volume pre-filter

Candles cost **one call per symbol**, so the universe is cut down before any are
requested, using only the ticker already in memory: crypto perpetuals in `TRADING` status
whose 24h quote volume clears `Scan:MinQuoteVolume24h`, minus anything in cooldown, capped
at `Scan:MaxSymbols`. At the 5,000,000 USDT default that is ~172 of 703 USDT pairs.

Binance publishes no market cap, so 24h quote volume stands in for it — it predicts pumps
better anyway and comes free with the ticker. Note that a floor in the hundreds of
thousands would filter nothing here: the 5th percentile of futures volume is already
614k USDT.

### The forming candle

Indicators are always computed on **closed** candles. The candle still forming is also
tested (`Scan:EvaluateFormingCandle`, on by default), comparing its partial volume against
the average of *whole* candles — a comparison that can only understate a spike, so it
finds the same moves sooner without inventing new ones. It cuts the delay from a full
candle to one scan interval. It is **not covered by the backtest above**; set it to false
to stay strictly on the measured path.

### Latency and the scan loop

A single-shot run on a 15-minute cron would report a 5-minute candle up to twenty minutes
late, which defeats the point. So a run **keeps scanning**: every `Scan:IntervalSeconds`
(60) for up to `Scan:MaxRunMinutes` (13), then exits and lets the cron start the next one.

Both modules run on every scan off the same ticker, so the 24h module became ~13× more
responsive at no extra cost. `Scan:IntervalSeconds = 0` restores the old one-scan-and-exit
behaviour exactly.

## Log

Everything printed to the console is also written to `Logs\pumps-<yyyy-MM-dd>.log`,
next to the executable: one file per day, one `yyyy-MM-dd HH:mm:ss [LEVEL] message`
line per entry.

Events that get logged: the start and end of each run (with its exit code), every
symbol notified via Telegram (saying whether it's crypto or a tokenized stock), every
symbol that drops below its threshold and is removed from its JSON, pairs discarded for
being suspended, and any exception. For the early module: the universe size after the
pre-filter, the Binance weight consumed, and one line per signal with the numbers that
qualified it.

Because a run scans a dozen times, **bookkeeping lines are logged once, events every
time.** Universe sizes, cooldown notes and "already notified" lists appear on the first
scan only; the 24h classification lines reappear whenever the candidate set changes.

If the folder can't be written to, file logging turns itself off and the program keeps
going on the console: failing to log can never be allowed to block a pump alert.

On startup, logs older than `Logging:RetentionDays` are deleted (30 by default, `0`
keeps them all). Age comes from **the date in the file name**, not its modification
date, so copying the folder doesn't make old logs look fresh. Files that don't match
the `pumps-<yyyy-MM-dd>.log` pattern are left untouched.

## One alert per symbol

Each state file maps symbol to **when its Telegram message went out**:

```json
{ "HEMIUSDT": "2026-08-21T13:22:04+00:00" }
```

In the **24h module**, a symbol's entry survives while **either** of these holds, and only
disappears when both stop being true:

- it's still above its threshold, or
- its `Filter:CooldownHours` cooldown (8h by default) hasn't expired yet.

Two rules fall out of that:

- **A sustained pump produces one message.** A coin three days above +100% is announced
  once, because its entry never leaves the file.
- **Flapping produces one message.** A coin that crosses the threshold, dips, and crosses
  again minutes later stays in the file the whole time, so it isn't re-announced. This is
  the reason the cooldown exists: before it, the dip erased the memory and the second
  crossing counted as new, producing two or three messages for the same coin.

The **early module's rule is simpler**: its signal is a moment, not a state that lasts all
day, so there is nothing to stay "still true" and the cooldown alone decides. An entry
lives for `Scan:CooldownHours` (2h, shorter than the 24h module's because these fire on
intraday candles) and is then dropped. Symbols in cooldown are skipped *before* their
candles are requested, so the memory also saves API calls.

**The timestamp is never refreshed.** It records when the message was sent, not when the
coin was last seen. Refreshing it would keep a sustained pump in cooldown forever, and
would rewrite the file on every run — which in the cloud means a git commit every 15
minutes. For the same reason the early module only writes its file when something actually
changed, rather than once a minute.

Saving happens **after** sending: if Telegram fails, nothing is recorded and the next scan
retries. Because each kind saves its own file right after its own send, a failure sending
one kind doesn't discard the other's progress. A corrupted file doesn't crash the program
— it's logged, ignored, and rewritten (the cost is a possible duplicate alert). A file
still in the old array-only format is migrated on read, stamping the current time on each
symbol.

**Only the last scan of a run decides the exit code.** The workflow skips the state commit
when a run fails, so failing over a transient Telegram error in scan 3 of 13 would throw
away the memory of everything already sent and re-announce all of it on the next run. If a
later scan succeeded, what's on disk is consistent and worth committing.

## Running it

```bash
dotnet run --project src/CoslyHighPriceBot
```

By default it scans every 60s for ~13 minutes and exits. Add `Scan__IntervalSeconds=0` for
a single pass. Exit codes: `0` success (whether or not any symbol matched), `1` error.

Useful overrides while working on it — everything is an environment variable:

```bash
# One pass, early module off: exactly how the bot behaved before that module existed.
Scan__Enabled=false Scan__IntervalSeconds=0 dotnet run --project src/CoslyHighPriceBot

# Force signals to check the plumbing end to end (send this somewhere harmless).
Scan__VolumeSpikeMultiplier=1.2 Scan__MinCandleBodyPercent=0 Scan__SqueezePercentile=1.0 \
Scan__RsiMin=0 Scan__RsiMax=100 dotnet run --project src/CoslyHighPriceBot
```

## Publishing for Task Scheduler

```bash
publish.cmd
```

Produces `publish\CoslyHighPriceBot.exe` (a single file, ~575 KB) next to its
`appsettings.json`. It's *framework-dependent*: it needs
the .NET 9 runtime on the machine. To make it runtime-independent, add
`--self-contained true` to the script (bumps the size to ~70 MB).

The program reads its configuration from `AppContext.BaseDirectory`, so **the working
directory** Task Scheduler launches it from doesn't matter.

Heads up: `publish\appsettings.json` is a copy. If you change a threshold there, you
also need to change it in `src\CoslyHighPriceBot\appsettings.json`, or the next
`publish.cmd` will overwrite it.

## Configuration

Every adjustable value lives in `src/CoslyHighPriceBot/appsettings.json`:

| Key | Description |
| --- | --- |
| `Binance:Ticker24hUrl` | Futures 24h ticker. With no query string it returns every symbol. |
| `Binance:ExchangeInfoUrl` | Symbol status and `contractType` (which classifies each symbol). |
| `Binance:KlinesUrl` | Candles, one call per symbol. Only the early module uses it. |
| `Binance:QuoteAsset` | Quote asset to filter by (symbol suffix). |
| `Binance:OnlyTradingSymbols` | Discards suspended pairs (see below). |
| `Filter:MinChangePercent` | Minimum 24h gain, in %, for **crypto**. |
| `Filter:StockMinChangePercent` | Minimum 24h gain, in %, for **tokenized stocks**. |
| `Filter:CooldownHours` | Hours before the same symbol can be alerted again. `0` disables it. |
| `State:NotifiedSymbolsFile` | Already-notified crypto. Relative = next to the executable. |
| `State:NotifiedStocksFile` | Already-notified tokenized stocks. Must differ from the above. |
| `State:NotifiedEarlyFile` | Early-pump memory. Must differ from the other two. |
| `Logging:RetentionDays` | Days of logs to keep. `0` = never delete any. |
| `Telegram:ApiBaseUrl` | Bot API base URL. |
| `Telegram:BotToken` | Bot token. **Secret.** |
| `Telegram:ChatIds` | Comma-separated destination chats; the same message goes to each. Private = positive ID; group/supergroup/channel = **negative**. |
| `Telegram:EarlyChatIds` | Destination chats for the early-pump alerts. **Empty falls back to `ChatIds`** — that's how the two streams get merged into one channel later, by clearing a value rather than changing code. |

Everything under `Scan:` belongs to the early module:

| Key | Default | Description |
| --- | --- | --- |
| `Scan:Enabled` | `true` | Off leaves the 24h alerts exactly as they were. |
| `Scan:IntervalSeconds` | `60` | Seconds between scans. `0` = one scan and exit. |
| `Scan:MaxRunMinutes` | `13` | How long a run keeps scanning. Kept under the 15-minute cron. |
| `Scan:KlineInterval` | `5m` | Candle size. |
| `Scan:KlineLimit` | `150` | Candles per symbol. Must exceed `SqueezeLookback + BollingerPeriod`. |
| `Scan:MinQuoteVolume24h` | `5000000` | Liquidity floor. **The pre-filter that keeps the call count sane.** |
| `Scan:MaxQuoteVolume24h` | `0` | Optional ceiling to skip mega caps. `0` = none. |
| `Scan:MaxSymbols` | `200` | Hard cap on candle calls per scan; most liquid kept. |
| `Scan:MaxConcurrentRequests` | `6` | Candle requests in flight at once. |
| `Scan:BollingerPeriod` / `Scan:BollingerStdDev` | `20` / `2` | Band window and width. |
| `Scan:SqueezeLookback` | `96` | Candles the squeeze is measured against (8h at 5m). |
| `Scan:SqueezePercentile` | `0.20` | How tight the bands must have been. `1` disables the test. |
| `Scan:VolumeAvgPeriod` | `20` | Candles averaged for the volume baseline. |
| `Scan:VolumeSpikeMultiplier` | `3` | How many times the baseline the candle must be. |
| `Scan:RsiPeriod` / `Scan:RsiMin` / `Scan:RsiMax` | `14` / `60` / `85` | RSI window and the band it has to sit in. |
| `Scan:MinCandleBodyPercent` | `1.5` | Minimum open-to-close move of the triggering candle. |
| `Scan:EvaluateFormingCandle` | `true` | Also test the candle in progress. `false` = only the measured path. |
| `Scan:CooldownHours` | `2` | Hours before the same symbol can fire an early alert again. |

`appsettings.json` is in `.gitignore` because it contains the token.

`appsettings.ci.json` is the credential-free copy that **is** version-controlled: it
serves as a template for a fresh install and, more importantly, is the configuration
that governs the runs on GitHub Actions (the workflow copies it to `appsettings.json`
before running). To change a threshold in the cloud, edit that file and commit.

Any key can be overridden with an **environment variable** using a double underscore
as the separator: `Telegram__BotToken`, `Filter__StockMinChangePercent`,
`State__NotifiedStocksFile`. They're read after the JSON, so they take priority.
This is how the token is passed in the cloud without writing it to any file.

## Running in the cloud (GitHub Actions)

[`.github/workflows/pump-alert.yml`](.github/workflows/pump-alert.yml) runs the bot
every 15 minutes without depending on any machine being on.

It needs three secrets in the repo (Settings → Secrets and variables → Actions):
`TELEGRAM_BOT_TOKEN`, `TELEGRAM_CHAT_IDS` and `TELEGRAM_EARLY_CHAT_IDS`. The last one is
the early-pump channel; leaving it unset sends those alerts to `TELEGRAM_CHAT_IDS` too.

The state lives in `state/notified-symbols.json`, `state/notified-stocks.json` and
`state/notified-early.json`, **version-controlled on purpose**: it's the only way for the
bot's memory to survive between runs, since the runner is ephemeral. The workflow commits
all three at the end of each run, and only if the send succeeded.

The runner's `Logs/` folder is lost when it finishes, so the workflow uploads the
day's file as an **artifact** (`log-<run number>`, 90-day retention). Download it from
the run's page. The same content is in the output of the "Find pumps and alert" step.
The artifact is uploaded with `always()`: it matters most when the run fails.

**Cost.** A run now lasts ~14 minutes instead of ~1, because it scans in a loop rather
than exiting immediately: roughly 40,000 billable minutes a month. That is free **only
while the repo is public**. If it ever goes private, set `Scan:MaxRunMinutes` to `0` (one
scan per run) or space the cron out.

GitHub delays scheduled crons under load, so the actual interval can run noticeably
longer than 15 minutes. The `concurrency` group prevents two runs overlapping, and
`Scan:MaxRunMinutes: 13` leaves margin against the 15-minute schedule.

## Structure

```
src/CoslyHighPriceBot/
├─ Program.cs                    bootstrap + scan loop; runs both modules off one ticker
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

**Binance calls per scan.** With the early module off: **1**, or 2 when something clears
the lower 24h threshold. With it on: 1 ticker + 1 exchangeInfo (once per run, cached) +
one candle call per symbol in the universe — about 172. Measured weight consumed: **~385
of the 2400/minute budget**, at 60-second scans. Candles cost weight 1 up to 100 and 2 up
to 500, which is why `KlineLimit` is 150 and not 500.

No DI or Generic Host: two modules constructed by hand in `Program.cs` don't need either.
`global.json` pins SDK 9.0.317 because the machine defaults to a .NET 10 preview.

## Things to keep in mind

- **`www.binance.com/fapi/...`, not `fapi.binance.com`**: measured from a GitHub runner,
  `fapi.binance.com` → **451**, `dapi.binance.com` → 451, `api.binance.com` → 451,
  `fapi1/2/3.binance.com` → 202 with an empty body (a block page), but
  **`www.binance.com/fapi/v1/...` → 200** and serves the full futures API. That single
  host is what makes running on GitHub Actions possible; without it the runner can't
  reach futures at all.
- **Futures `exchangeInfo` takes no `symbols` filter.** It always returns the full
  catalog (~1 MB), which is why `SymbolMetadataCache` fetches it **once per run** and
  shares it. Because it's lazy, a quiet run with the early module off still costs exactly
  one Binance call, the way it always did.
- **Klines are the only per-symbol call, and the only one that can hit a rate limit.**
  `GetJsonAsync` retries 429/418 (honouring `Retry-After`) three times and then throws
  `BinanceRateLimitException`, which abandons that one scan without taking down the run or
  the 24h module. Requests are capped by a `SemaphoreSlim`.
- **Klines don't look like the other endpoints.** They come back as an array of arrays of
  mixed types, so `[JsonPropertyName]` has nothing to bind to and `Kline.TryFromArray`
  maps by position. A row that doesn't parse is dropped, not thrown.
- Binance returns **every numeric field as a string**; parsing uses
  `CultureInfo.InvariantCulture` (see `CoinFilter`).
- **`Indicators` works in `double`, not `decimal`**, unlike the rest of the code: a
  standard deviation needs a square root and `decimal` has no operator for it. Verified
  against the Python prototype the thresholds were measured with — RSI, volume ratio and
  squeeze rank matched to the digit on live data.
- **Suspended pairs**: symbols in `BREAK` or `HALT` status keep their 24h stats
  frozen, so they show up as huge pumps that can't actually be traded. In a real test,
  4 of 9 coins above the threshold were in `BREAK`. That's why `OnlyTradingSymbols`
  defaults to `true`.
- **Watch out for collection defaults in configuration**: the
  `Microsoft.Extensions.Configuration` binder calls `Add()` on the property's existing
  list instead of replacing it, so a `List<T>` option must never be given a non-empty
  default — the JSON values get appended to it rather than replacing it.
- **Sending to a group or several destinations at once**: no code changes needed, just
  list the IDs in `Telegram:ChatIds`, comma-separated (`"-100111,-100222"`). Each
  configured message (crypto, tokenized stocks) is sent once per chat in that list. To
  find a group's or channel's ID: add the bot as a member (a channel needs it as
  admin), send `/start@<bot>` there for a group (with privacy mode on, the bot only
  sees messages that start with `/` or mention it; not needed for a channel post), and
  read `message.chat.id` from `https://api.telegram.org/bot<TOKEN>/getUpdates`. If a
  regular group is upgraded to a supergroup, the ID changes and needs to be updated.
- **Multi-chat send stops at the first failure**: if chat 1 succeeds and chat 2 fails,
  nothing is saved, so the next scan retries — which resends to chat 1 too. That's a
  deliberate trade-off: a rare duplicate is safer than silently never reaching a
  configured destination.
- **`AppLog` takes a lock.** The early module fetches candles in parallel, so log lines
  arrive from several threads; without it two concurrent appends collide, the write
  throws, and file logging switches itself off for the rest of the run.
- The message uses `parse_mode: HTML`, so all dynamic text goes through the escaping
  in `MessageFormatter.Escape` (`&`, `<`, `>`).
- The token is part of the Telegram URL: it must never show up in logs or exceptions.
- Prices range from the tens of thousands down to 0.00000001, which is why
  `FormatPrice` switches format based on magnitude instead of using a fixed number of decimals.
