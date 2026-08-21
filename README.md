# COSLY High Price Bot

A .NET 9 console app that watches Binance for pumps: it fetches the 24-hour ticker,
keeps the `*USDT` pairs that gained more than a configurable threshold, and sends a
formatted alert to Telegram with price, open, high/low, volume and trade count.

It tracks **two kinds of asset separately**, because they don't move on the same scale:

| Kind | Default threshold | Message | State file |
| --- | --- | --- | --- |
| Crypto | +100% in 24h | 🚀 Crypto Pumps | `notified-symbols.json` |
| Tokenized stocks | +20% in 24h | 📈 Tokenized Stocks | `notified-stocks.json` |

If nothing crosses its threshold, **no message is sent** — the run just logs that
nothing matched and exits cleanly. A symbol is announced once and then held quiet: while
it stays above its threshold, and for a configurable cooldown (8h by default) after the
alert. That cooldown is what stops a coin that dips and re-crosses the threshold minutes
later from being announced two or three times.

Runs as a one-shot process, so it fits equally well in a Windows Task Scheduler job
or a GitHub Actions cron — no server, no database, no background process to keep alive.

## Features

- Polls Binance's public 24h ticker and filters by quote asset and minimum % gain
- Separates tokenized equities (`AAPLBUSDT`, `TSLABUSDT`, `SNDKBUSDT`) from crypto and
  applies a much lower threshold to them, since a +15% day for a stock is exceptional
- Skips symbols with suspended trading (`BREAK`/`HALT`), which would otherwise show
  up as huge, untradeable "pumps"
- Sends a single alert per symbol — no repeats while it stays above its threshold,
  plus a cooldown (8h by default) so a dip-and-recross doesn't re-announce it
- Daily rotating log file with automatic retention cleanup
- Everything configurable via `appsettings.json` or environment variables (for secrets)
- Ready-to-use GitHub Actions workflow to run every 15 minutes for free
- **Two Binance calls per run at most**, and usually just one

## Quick start

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

1. Copy `src/CoslyHighPriceBot/appsettings.ci.json` to
   `src/CoslyHighPriceBot/appsettings.json` and fill in your Telegram bot token and chat ID.
2. Run it:

   ```bash
   dotnet run --project src/CoslyHighPriceBot
   ```

It runs once, alerts if there's anything to alert about, and exits. Exit code `0` on
success (whether or not anything matched), `1` on error.

### Getting a Telegram bot token and chat ID

1. Talk to [@BotFather](https://t.me/BotFather) on Telegram, run `/newbot`, and copy
   the token it gives you.
2. For a private chat: send your bot any message, then open
   `https://api.telegram.org/bot<TOKEN>/getUpdates` and read `message.chat.id`.
3. For a group: add the bot to the group, send `/start@<bot>` there, and read
   `message.chat.id` from the same URL. Group IDs are **negative**.

## How tokenized stocks are detected

Binance's spot market lists tokenized equities with a `B` suffix on the base asset:
`AAPLB`, `TSLAB`, `SNDKB`. Spot's `exchangeInfo` gives **no field** that identifies
them — every symbol looks alike.

Matching "base asset ends in `B`" is tempting and **wrong**: it also catches
**BNB, SHIB, ARB, DGB, TRB, CKB, BB, YB and QNTB**.

So the bot uses a version-controlled catalog,
[`src/CoslyHighPriceBot/tokenized-stocks.json`](src/CoslyHighPriceBot/tokenized-stocks.json),
listing the base assets that really are tokenized stocks. A newly listed stock that
isn't in the catalog is simply treated as crypto — it won't alert until +100%, but it
never produces a wrong alert.

### Regenerating the catalog

The authoritative source is Binance **futures**, where the classification is explicit
(`contractType: "TRADIFI_PERPETUAL"`). This PowerShell snippet cross-references it
against spot and rewrites the catalog:

```powershell
$f = Invoke-RestMethod "https://fapi.binance.com/fapi/v1/exchangeInfo"
$tradfi = ($f.symbols | Where-Object { $_.contractType -eq 'TRADIFI_PERPETUAL' }).baseAsset | Sort-Object -Unique
$r = Invoke-RestMethod "https://data-api.binance.vision/api/v3/exchangeInfo"
$list = ($r.symbols | Where-Object { $_.quoteAsset -eq 'USDT' -and $_.baseAsset.EndsWith('B') -and $tradfi -contains $_.baseAsset.Substring(0, $_.baseAsset.Length - 1) }).baseAsset | Sort-Object -Unique
$list | ConvertTo-Json | Set-Content -Encoding utf8 src/CoslyHighPriceBot/tokenized-stocks.json
```

> Run it from an unrestricted IP. `fapi.binance.com` returns `451` from US datacenters,
> including GitHub Actions runners — which is exactly why the catalog is committed
> rather than fetched at runtime.

## Configuration

Every adjustable value lives in `appsettings.json`:

| Key | Description |
| --- | --- |
| `Binance:Ticker24hUrl` | 24h ticker endpoint. With no query string it returns every symbol. |
| `Binance:ExchangeInfoUrl` | Each symbol's status (TRADING / BREAK / HALT). |
| `Binance:QuoteAsset` | Quote asset to filter by (symbol suffix), e.g. `USDT`. |
| `Binance:OnlyTradingSymbols` | Discards suspended pairs (recommended: `true`). |
| `Filter:MinChangePercent` | Minimum 24h gain, in %, for crypto. |
| `Filter:StockMinChangePercent` | Minimum 24h gain, in %, for tokenized stocks. |
| `Filter:CooldownHours` | Hours before the same symbol can be alerted again. `0` disables it. |
| `State:NotifiedSymbolsFile` | Tracks already-notified crypto. |
| `State:NotifiedStocksFile` | Tracks already-notified tokenized stocks. |
| `State:TokenizedStocksFile` | Read-only catalog of tokenized-stock base assets. |
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

The bot's memory (`state/notified-symbols.json` and `state/notified-stocks.json`) is
committed back to the repo after each run, since GitHub Actions runners are ephemeral
and don't persist disk state between runs on their own. Each run's log is also uploaded
as a downloadable artifact (90-day retention), particularly useful when a run fails.

> **Why `data-api.binance.vision` and not `api.binance.com`?** The main Binance
> domain returns `451 Unavailable For Legal Reasons` from US datacenter IPs — which
> is where GitHub Actions runners live. `data-api.binance.vision` is Binance's public,
> read-only market-data mirror and works fine from CI. Note there is no equivalent
> host for the futures API, so futures data can't be used from GitHub Actions at all.

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
├─ Program.cs                    orchestration: config → fetch → filter → alert per kind
├─ Configuration/AppSettings.cs  appsettings.json POCOs + validation
├─ Models/Ticker24h.cs           Binance DTOs (all strings) + CoinKind and Coin
├─ tokenized-stocks.json         catalog of tokenized-stock base assets
└─ Services/
   ├─ BinanceClient.cs           24h ticker and symbol status
   ├─ CoinFilter.cs              classifies by kind and applies each kind's threshold
   ├─ MessageFormatter.cs        HTML message text, split if it exceeds 4096 chars
   ├─ TelegramNotifier.cs        POST to sendMessage
   ├─ SymbolSetStore.cs          reads the tokenized-stock catalog
   ├─ AlertHistoryStore.cs       reads/writes symbol -> last-alerted timestamp
   └─ AppLog.cs                  console + daily file in Logs/
```

No dependency injection or generic host — it's a single-shot CLI program and doesn't
need either. `global.json` pins the SDK version.

## Notes on Binance's API

- Every numeric field in Binance's responses comes back as a string; parsing uses
  `CultureInfo.InvariantCulture` throughout.
- Suspended pairs (`BREAK`/`HALT`) keep their 24h stats frozen, so they can look like
  enormous pumps that are actually untradeable — that's what `OnlyTradingSymbols`
  guards against.

## License

No license file yet — all rights reserved by default until one is added. If you'd
like to use or fork this project, consider opening an issue to ask about adding an
open-source license (MIT is a common, permissive choice).
