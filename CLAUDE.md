# COSLY_HIGH_PRICE_BOT

.NET 9 console app that detects "pumps" on Binance: it queries the 24-hour ticker,
keeps the `*USDT` pairs that rose more than a configurable threshold (100% by
default), and sends a single formatted alert to Telegram.

If no coin exceeds the threshold, **nothing is sent** — it's just logged to the console.

## Log

Everything printed to the console is also written to `Logs\pumps-<yyyy-MM-dd>.log`,
next to the executable: one file per day, one `yyyy-MM-dd HH:mm:ss [LEVEL] message`
line per entry.

Events that get logged: the start and end of each run (with its exit code), every
symbol notified via Telegram, every symbol that drops below the threshold and is
removed from the JSON, pairs discarded for being suspended, and any exception.

If the folder can't be written to, file logging turns itself off and the program
keeps going on the console: failing to log can never be allowed to block a pump alert.

On startup, logs older than `Logging:RetentionDays` are deleted (30 by default, `0`
keeps them all). Age comes from **the date in the file name**, not its modification
date, so copying the folder doesn't make old logs look fresh. Files that don't match
the `pumps-<yyyy-MM-dd>.log` pattern are left untouched.

## One alert per symbol

Every notified symbol is saved to `notified-symbols.json` (a JSON array, next to the
executable) and won't be notified again while it stays above the threshold. Once it
drops, it's removed from the file, so if it pumps again later it gets notified again.

Each run's cycle:

1. Read the file (if it doesn't exist, start with an empty list).
2. Compute the coins that are above the threshold today and tradable.
3. Remove from the file the ones no longer in that list.
4. Notify only the ones that weren't already in the file.
5. Save the file with the coins from step 2.

Saving happens **after** sending: if Telegram fails, the run ends with exit code `1`
without recording anything, and the next run retries. A corrupted file doesn't crash
the program — it's logged, ignored, and rewritten (the cost is a possible duplicate alert).

## Running it

```bash
dotnet run --project src/CoslyHighPriceBot
```

Single run: it runs, alerts, and exits. Exit codes: `0` success (whether or not any
coin matched), `1` error.

## Publishing for Task Scheduler

```bash
publish.cmd
```

Produces `publish\CoslyHighPriceBot.exe` (a single file, ~575 KB) next to its
`appsettings.json`. It's *framework-dependent*: it needs the .NET 9 runtime on the
machine. To make it runtime-independent, add `--self-contained true` to the script
(bumps the size to ~70 MB).

The program reads its configuration from `AppContext.BaseDirectory`, so **the working
directory** Task Scheduler launches it from doesn't matter.

Heads up: `publish\appsettings.json` is a copy. If you change the threshold there,
you also need to change it in `src\CoslyHighPriceBot\appsettings.json`, or the next
`publish.cmd` will overwrite it.

## Configuration

Every adjustable value lives in `src/CoslyHighPriceBot/appsettings.json`:

| Key | Description |
| --- | --- |
| `Binance:Ticker24hUrl` | 24h ticker endpoint. With no query string it returns every symbol. |
| `Binance:RollingTickerUrl` | Rolling-window ticker; this is where the 4h and 1h percentages come from. |
| `Binance:ExchangeInfoUrl` | Each symbol's status (TRADING / BREAK / HALT). |
| `Binance:QuoteAsset` | Quote asset to filter by (symbol suffix). |
| `Binance:ExtraWindows` | Short windows to show in addition to the 24h one, in order. E.g.: `["4h", "1h"]`. |
| `Binance:OnlyTradingSymbols` | Discards suspended pairs (see below). |
| `Filter:MinChangePercent` | Minimum 24h gain, in %, to make it into the alert. |
| `State:NotifiedSymbolsFile` | File with the already-notified symbols. Relative = next to the executable. |
| `Logging:RetentionDays` | Days of logs to keep. `0` = never delete any. |
| `Telegram:ApiBaseUrl` | Bot API base URL. |
| `Telegram:BotToken` | Bot token. **Secret.** |
| `Telegram:ChatId` | Destination chat. Private = positive ID; group/supergroup = **negative**. |

`appsettings.json` is in `.gitignore` because it contains the token.

`appsettings.ci.json` is the credential-free copy that **is** version-controlled: it
serves as a template for a fresh install and, more importantly, is the configuration
that governs the runs on GitHub Actions (the workflow copies it to `appsettings.json`
before running). To change the threshold in the cloud, edit that file and commit.

Any key can be overridden with an **environment variable** using a double underscore
as the separator: `Telegram__BotToken`, `Filter__MinChangePercent`,
`State__NotifiedSymbolsFile`. They're read after the JSON, so they take priority.
This is how the token is passed in the cloud without writing it to any file.

## Running in the cloud (GitHub Actions)

[`.github/workflows/pump-alert.yml`](.github/workflows/pump-alert.yml) runs the bot
every 15 minutes without depending on any machine being on.

It needs two secrets in the repo (Settings → Secrets and variables → Actions):
`TELEGRAM_BOT_TOKEN` and `TELEGRAM_CHAT_ID`.

The state lives in `state/notified-symbols.json`, **version-controlled on purpose**:
it's the only way for the bot's memory to survive between runs, since the runner is
ephemeral. The workflow commits it at the end of each run, and only if the send succeeded.

The runner's `Logs/` folder is lost when it finishes, so the workflow uploads the
day's file as an **artifact** (`log-<run number>`, 90-day retention). Download it from
the run's page. The same content is in the output of the "Find pumps and alert" step.
The artifact is uploaded with `always()`: it matters most when the run fails.

It's free **if the repo is public**: every run costs a minimum of 1 billable minute,
and 2,880 runs a month go past the 2,000-minute free plan for private repos.

GitHub delays scheduled crons under load, so the actual interval can run noticeably
longer than 15 minutes.

## Structure

```
src/CoslyHighPriceBot/
├─ Program.cs                    orchestration: config → fetch → filter → alert
├─ Configuration/AppSettings.cs  appsettings.json POCOs + validation
├─ Models/Ticker24h.cs           Binance DTOs (all strings) + Coin and WindowChange records
└─ Services/
   ├─ BinanceClient.cs           24h ticker, rolling windows, and symbol status
   ├─ CoinFilter.cs              filtering by quote asset and %, sorted descending
   ├─ MessageFormatter.cs        HTML message text, split if it exceeds 4096 chars
   ├─ TelegramNotifier.cs        POST to sendMessage
   ├─ NotifiedSymbolStore.cs     reads/writes notified-symbols.json
   └─ AppLog.cs                  console + daily file in Logs/
```

Binance calls per run: **1** if no coin exceeds the threshold (the usual case). If one
does, add 1 for `exchangeInfo`; and only if there are new coins to notify, 1 more per
`ExtraWindows` entry (with all symbols batched into the `symbols` parameter).

No DI or Generic Host: it's a single-shot program and doesn't need either.
`global.json` pins SDK 9.0.317 because the machine defaults to a .NET 10 preview.

## Things to keep in mind

- **`data-api.binance.vision`, not `api.binance.com`**: the main domain responds with
  `451 Unavailable For Legal Reasons` from US datacenter IPs, which is where GitHub
  Actions runners live. `data-api.binance.vision` is Binance's public market-data
  endpoint (read-only, no API key) and serves all three endpoints this project uses.
- Binance returns **every numeric field as a string**; parsing uses
  `CultureInfo.InvariantCulture` (see `CoinFilter`).
- **Suspended pairs**: symbols in `BREAK` or `HALT` status keep their 24h stats
  frozen, so they show up as huge pumps that can't actually be traded. In a real test,
  4 of 9 coins above the threshold were in `BREAK`. That's why `OnlyTradingSymbols`
  defaults to `true`.
- **Watch out for collection defaults in configuration**: the
  `Microsoft.Extensions.Configuration` binder calls `Add()` on the property's existing
  list instead of replacing it. `ExtraWindows` starts as `[]` for that reason; setting
  `["4h", "1h"]` as its default would end up with all four values duplicated.
- If Binance doesn't return a symbol in a window's response, it's shown as `0%` and a
  `WARN` is logged: without that trace it's indistinguishable from "didn't move".
- **Sending to a group**: no code changes needed, just put the group's ID in
  `Telegram:ChatId`. To find it: add the bot to the group, send `/start@<bot>` there
  (with privacy mode on, the bot only sees messages that start with `/` or mention
  it), and read `message.chat.id` from
  `https://api.telegram.org/bot<TOKEN>/getUpdates`. If a regular group is upgraded to
  a supergroup, the ID changes and needs to be updated.
- The message uses `parse_mode: HTML`, so all dynamic text goes through the escaping
  in `MessageFormatter.Escape` (`&`, `<`, `>`).
- The token is part of the Telegram URL: it must never show up in logs or exceptions.
- Prices range from the tens of thousands down to 0.00000001, which is why
  `FormatPrice` switches format based on magnitude instead of using a fixed number of decimals.
