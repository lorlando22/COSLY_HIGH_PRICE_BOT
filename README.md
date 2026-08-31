# COSLY High Price Bot

A .NET 9 console app that watches **Binance USD-M futures** for pumps and alerts on
Telegram. It detects symbols whose 24-hour change cleared a threshold, crypto and
tokenized stocks each with their own threshold, message and state file:

| Detector | Question | Default | Message | State file |
| --- | --- | --- | --- | --- |
| **24h pump** — crypto | What already moved a lot today? | +100% in 24h | 🚀 Crypto Pumps | `notified-symbols.json` |
| **24h pump** — tokenized stocks | Same, on a scale equities actually reach | +20% in 24h | 📈 Tokenized Stocks | `notified-stocks.json` |

If nothing crosses its threshold, **no message is sent** — the run just logs that
nothing matched and exits cleanly. A symbol is announced once and then held quiet: while
it stays above its threshold, and for a configurable cooldown (8h by default). That
cooldown is what stops a coin that dips and re-crosses the threshold minutes later from
being announced two or three times.

No server, no database. A run scans in a loop for ~13 minutes and exits, so it fits a
GitHub Actions cron or a Windows Task Scheduler job — which it still is with
`Run:IntervalSeconds = 0` for a single pass.

> **Looking for a pump detector that catches the move as it starts, instead of after a
> 24h threshold trips?** That's [`COSLY_EARLY_PUMP_BOT`](../COSLY_EARLY_PUMP_BOT), a
> sibling repo. It used to be a second module in this project; it was split out because
> the two are different products — different thresholds, messages, state and cooldowns
> — sharing only the idea of reading Binance and alerting on Telegram.

## Features

- Polls Binance's public futures 24h ticker and filters by quote asset and minimum % gain
- Separates tokenized equities (`TSLAUSDT`, `MRNAUSDT`, `HOODUSDT`) from crypto using the
  exchange's own `contractType`, and applies a much lower threshold to them, since a +15%
  day for a stock is exceptional
- Skips symbols with suspended trading (`BREAK`/`HALT`), which would otherwise show
  up as huge, untradeable "pumps"
- Sends a single alert per symbol — no repeats while it stays above its threshold,
  plus a cooldown (8h by default) so a dip-and-recross doesn't re-announce it
- Scans in a loop rather than exiting immediately, so a symbol crossing the threshold
  is caught within a minute instead of up to fifteen
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
exits. Add `Run__IntervalSeconds=0` for a single pass. Exit code `0` on success (whether
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

## Configuration

Every adjustable value lives in `appsettings.json`:

| Key | Description |
| --- | --- |
| `Binance:Ticker24hUrl` | Futures 24h ticker. With no query string it returns every symbol. |
| `Binance:ExchangeInfoUrl` | Symbol status and `contractType` (which classifies each symbol). |
| `Binance:QuoteAsset` | Quote asset to filter by (symbol suffix), e.g. `USDT`. |
| `Binance:OnlyTradingSymbols` | Discards suspended pairs (recommended: `true`). |
| `Filter:MinChangePercent` | Minimum 24h gain, in %, for crypto. |
| `Filter:StockMinChangePercent` | Minimum 24h gain, in %, for tokenized stocks. |
| `Filter:CooldownHours` | Hours before the same symbol can be alerted again. `0` disables it. |
| `Run:IntervalSeconds` | Seconds between scans. `0` = one scan and exit. Default `60`. |
| `Run:MaxRunMinutes` | How long a run keeps scanning. Default `13`. |
| `State:NotifiedSymbolsFile` | Tracks already-notified crypto. |
| `State:NotifiedStocksFile` | Tracks already-notified tokenized stocks. |
| `Logging:RetentionDays` | Days of logs to keep. `0` = keep them all. |
| `Telegram:ApiBaseUrl` | Bot API base URL. |
| `Telegram:BotToken` | Your bot's token. **Secret — never commit this.** |
| `Telegram:ChatIds` | Comma-separated destination chats. Private = positive ID; group/channel = **negative**. |

Any key can be overridden with an environment variable using a double underscore as
the separator, e.g. `Telegram__BotToken`, `Filter__StockMinChangePercent`. This is how
secrets are passed in the cloud without writing them to a file.

`appsettings.json` (with your real token) is gitignored. `appsettings.ci.json` is a
credential-free template you can copy from.

## Deploying to GitHub Actions (free)

The included [`.github/workflows/pump-alert.yml`](.github/workflows/pump-alert.yml)
runs the bot every 15 minutes without needing any machine to stay on.

1. Fork or push this repo to GitHub — **keep it public** so Actions minutes are free
   (2,880 runs/month exceeds the private-repo free tier).
2. Add two repository secrets (Settings → Secrets and variables → Actions):
   `TELEGRAM_BOT_TOKEN` and `TELEGRAM_CHAT_IDS`.
3. Trigger it once manually from the Actions tab to confirm it works, or just wait
   for the next scheduled run.

The bot's memory (the two files under `state/`) is committed back to the repo after each
run, since GitHub Actions runners are ephemeral and don't persist disk state between runs
on their own. Each run's log is also uploaded as a downloadable artifact (90-day
retention), particularly useful when a run fails.

> **Keeping the repo public matters more now.** A run scans for ~13 minutes instead of
> exiting immediately, so it bills ~14 minutes rather than 1 — roughly 40,000 minutes a
> month. That's free on a public repo and expensive on a private one; set
> `Run:MaxRunMinutes` to `0` there to go back to one scan per run.

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
├─ Program.cs                    bootstrap + scan loop
├─ Configuration/AppSettings.cs  appsettings.json POCOs + validation
├─ Modules/
│  └─ DailyPumpModule.cs         the detector: crypto and tokenized stocks
├─ Models/
│  └─ Ticker24h.cs               Binance DTOs (all strings) + CoinKind and Coin
└─ Services/
   ├─ BinanceClient.cs           24h ticker, symbol metadata, 429/418 backoff
   ├─ SymbolMetadataCache.cs     fetches exchangeInfo once per run and shares it
   ├─ CoinFilter.cs              candidates, then classify by contractType + per-kind threshold
   ├─ MessageFormatter.cs        HTML message text, split if it exceeds 4096 chars
   ├─ TelegramNotifier.cs        POST to sendMessage, per-chat send
   ├─ AlertHistoryStore.cs       reads/writes symbol -> last-alerted timestamp
   └─ AppLog.cs                  console + daily file in Logs/
```

No dependency injection or generic host — one module constructed by hand doesn't need
either. `global.json` pins the SDK version.

## Notes on Binance's API

- Every numeric field in Binance's responses comes back as a string; parsing uses
  `CultureInfo.InvariantCulture` throughout.
- Symbols not in `TRADING` status keep their 24h stats frozen, so they can look like
  enormous pumps that are actually untradeable — that's what `OnlyTradingSymbols`
  guards against.
- Futures `exchangeInfo` accepts no `symbols` filter and always returns the full ~1 MB
  catalog, so it's fetched once per run and cached. A quiet run still costs exactly one
  Binance call.

## License

No license file yet — all rights reserved by default until one is added. If you'd
like to use or fork this project, consider opening an issue to ask about adding an
open-source license (MIT is a common, permissive choice).
