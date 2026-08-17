# COSLY High Price Bot

A .NET 9 console app that watches Binance for pumps: it fetches the 24-hour ticker,
keeps the `*USDT` pairs that gained more than a configurable threshold (100% by
default), and sends a single formatted alert to Telegram — with 4h/1h momentum,
price, volume, and trade count for each coin.

If nothing crosses the threshold, **no message is sent** — the run just logs that
nothing matched and exits cleanly. Coins already notified aren't repeated until they
drop back below the threshold first.

Runs as a one-shot process, so it fits equally well in a Windows Task Scheduler job
or a GitHub Actions cron — no server, no database, no background process to keep alive.

## Features

- Polls Binance's public 24h ticker and filters by quote asset and minimum % gain
- Adds short-window momentum (e.g. 4h, 1h) for each matching coin
- Skips symbols with suspended trading (`BREAK`/`HALT`), which would otherwise show
  up as huge, untradeable "pumps"
- Sends a single alert per symbol — no repeats while it stays above the threshold
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

It runs once, alerts if there's anything to alert about, and exits. Exit code `0` on
success (whether or not any coin matched), `1` on error.

### Getting a Telegram bot token and chat ID

1. Talk to [@BotFather](https://t.me/BotFather) on Telegram, run `/newbot`, and copy
   the token it gives you.
2. For a private chat: send your bot any message, then open
   `https://api.telegram.org/bot<TOKEN>/getUpdates` and read `message.chat.id`.
3. For a group: add the bot to the group, send `/start@<bot>` there, and read
   `message.chat.id` from the same URL. Group IDs are **negative**.

## Configuration

Every adjustable value lives in `appsettings.json`:

| Key | Description |
| --- | --- |
| `Binance:Ticker24hUrl` | 24h ticker endpoint. With no query string it returns every symbol. |
| `Binance:RollingTickerUrl` | Rolling-window ticker; source of the 4h/1h percentages. |
| `Binance:ExchangeInfoUrl` | Each symbol's status (TRADING / BREAK / HALT). |
| `Binance:QuoteAsset` | Quote asset to filter by (symbol suffix), e.g. `USDT`. |
| `Binance:ExtraWindows` | Short windows to show besides 24h, in order. E.g.: `["4h", "1h"]`. |
| `Binance:OnlyTradingSymbols` | Discards suspended pairs (recommended: `true`). |
| `Filter:MinChangePercent` | Minimum 24h gain, in %, to trigger an alert. |
| `State:NotifiedSymbolsFile` | File that tracks already-notified symbols. |
| `Logging:RetentionDays` | Days of logs to keep. `0` = keep them all. |
| `Telegram:ApiBaseUrl` | Bot API base URL. |
| `Telegram:BotToken` | Your bot's token. **Secret — never commit this.** |
| `Telegram:ChatId` | Destination chat. Private = positive ID; group = **negative**. |

Any key can be overridden with an environment variable using a double underscore as
the separator, e.g. `Telegram__BotToken`, `Filter__MinChangePercent`. This is how
secrets are passed in the cloud without writing them to a file.

`appsettings.json` (with your real token) is gitignored. `appsettings.ci.json` is a
credential-free template you can copy from.

## Deploying to GitHub Actions (free)

The included [`.github/workflows/pump-alert.yml`](.github/workflows/pump-alert.yml)
runs the bot every 15 minutes without needing any machine to stay on.

1. Fork or push this repo to GitHub — **keep it public** so Actions minutes are free
   (2,880 runs/month exceeds the private-repo free tier).
2. Add two repository secrets (Settings → Secrets and variables → Actions):
   `TELEGRAM_BOT_TOKEN` and `TELEGRAM_CHAT_ID`.
3. Trigger it once manually from the Actions tab to confirm it works, or just wait
   for the next scheduled run.

The bot's memory (`state/notified-symbols.json`) is committed back to the repo after
each run, since GitHub Actions runners are ephemeral and don't persist disk state
between runs on their own. Each run's log is also uploaded as a downloadable artifact
(90-day retention), particularly useful when a run fails.

> **Why `data-api.binance.vision` and not `api.binance.com`?** The main Binance
> domain returns `451 Unavailable For Legal Reasons` from US datacenter IPs — which
> is where GitHub Actions runners live. `data-api.binance.vision` is Binance's public,
> read-only market-data mirror and works fine from CI.

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
├─ Program.cs                    orchestration: config → fetch → filter → alert
├─ Configuration/AppSettings.cs  appsettings.json POCOs + validation
├─ Models/Ticker24h.cs           Binance DTOs (all strings) + Coin/WindowChange records
└─ Services/
   ├─ BinanceClient.cs           24h ticker, rolling windows, and symbol status
   ├─ CoinFilter.cs              filtering by quote asset and %, sorted descending
   ├─ MessageFormatter.cs        HTML message text, split if it exceeds 4096 chars
   ├─ TelegramNotifier.cs        POST to sendMessage
   ├─ NotifiedSymbolStore.cs     reads/writes notified-symbols.json
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
- If Binance omits a symbol from a rolling-window response, it's shown as `0%` and
  logged as a warning, since that's otherwise indistinguishable from "didn't move".

## License

No license file yet — all rights reserved by default until one is added. If you'd
like to use or fork this project, consider opening an issue to ask about adding an
open-source license (MIT is a common, permissive choice).
