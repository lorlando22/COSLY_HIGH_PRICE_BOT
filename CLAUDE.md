# COSLY_HIGH_PRICE_BOT

.NET 9 console app that detects "pumps" on Binance **USD-M futures**: it queries the
24-hour ticker, keeps the `*USDT` perpetuals that rose more than a configurable
threshold, and sends a formatted alert to Telegram.

It handles **two kinds of asset separately**, each with its own threshold, its own
Telegram message and its own already-notified file:

| Kind | Threshold | State file |
| --- | --- | --- |
| Crypto | `Filter:MinChangePercent` (100% default) | `notified-symbols.json` |
| Tokenized stocks | `Filter:StockMinChangePercent` (20% default) | `notified-stocks.json` |

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

## Log

Everything printed to the console is also written to `Logs\pumps-<yyyy-MM-dd>.log`,
next to the executable: one file per day, one `yyyy-MM-dd HH:mm:ss [LEVEL] message`
line per entry.

Events that get logged: the start and end of each run (with its exit code), every
symbol notified via Telegram (saying whether it's crypto or a tokenized stock), every
symbol that drops below its threshold and is removed from its JSON, pairs discarded for
being suspended, and any exception.

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

A symbol's entry survives while **either** of these holds, and only disappears when both
stop being true:

- it's still above its threshold, or
- its `Filter:CooldownHours` cooldown (8h by default) hasn't expired yet.

Two rules fall out of that:

- **A sustained pump produces one message.** A coin three days above +100% is announced
  once, because its entry never leaves the file.
- **Flapping produces one message.** A coin that crosses the threshold, dips, and crosses
  again minutes later stays in the file the whole time, so it isn't re-announced. This is
  the reason the cooldown exists: before it, the dip erased the memory and the second
  crossing counted as new, producing two or three messages for the same coin.

**The timestamp is never refreshed.** It records when the message was sent, not when the
coin was last seen. Refreshing it would keep a sustained pump in cooldown forever, and
would rewrite the file on every run — which in the cloud means a git commit every 15
minutes.

Saving happens **after** sending: if Telegram fails, the run ends with exit code `1`
without recording anything, and the next run retries. Because each kind saves its own
file right after its own send, a failure sending one kind doesn't discard the other's
progress. A corrupted file doesn't crash the program — it's logged, ignored, and
rewritten (the cost is a possible duplicate alert). A file still in the old array-only
format is migrated on read, stamping the current time on each symbol.

## Running it

```bash
dotnet run --project src/CoslyHighPriceBot
```

Single run: it runs, alerts, and exits. Exit codes: `0` success (whether or not any
symbol matched), `1` error.

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
| `Binance:QuoteAsset` | Quote asset to filter by (symbol suffix). |
| `Binance:OnlyTradingSymbols` | Discards suspended pairs (see below). |
| `Filter:MinChangePercent` | Minimum 24h gain, in %, for **crypto**. |
| `Filter:StockMinChangePercent` | Minimum 24h gain, in %, for **tokenized stocks**. |
| `Filter:CooldownHours` | Hours before the same symbol can be alerted again. `0` disables it. |
| `State:NotifiedSymbolsFile` | Already-notified crypto. Relative = next to the executable. |
| `State:NotifiedStocksFile` | Already-notified tokenized stocks. Must differ from the above. |
| `Logging:RetentionDays` | Days of logs to keep. `0` = never delete any. |
| `Telegram:ApiBaseUrl` | Bot API base URL. |
| `Telegram:BotToken` | Bot token. **Secret.** |
| `Telegram:ChatIds` | Comma-separated destination chats; the same message goes to each. Private = positive ID; group/supergroup/channel = **negative**. |

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

It needs two secrets in the repo (Settings → Secrets and variables → Actions):
`TELEGRAM_BOT_TOKEN` and `TELEGRAM_CHAT_IDS`.

The state lives in `state/notified-symbols.json` and `state/notified-stocks.json`,
**version-controlled on purpose**: it's the only way for the bot's memory to survive
between runs, since the runner is ephemeral. The workflow commits both at the end of
each run, and only if the send succeeded.

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
├─ Program.cs                    orchestration: config → fetch → filter → alert per kind
├─ Configuration/AppSettings.cs  appsettings.json POCOs + validation
├─ Models/Ticker24h.cs           Binance DTOs (all strings) + CoinKind and Coin
└─ Services/
   ├─ BinanceClient.cs           futures 24h ticker and symbol metadata
   ├─ CoinFilter.cs              candidates, then classify by contractType + per-kind threshold
   ├─ MessageFormatter.cs        HTML message text, split if it exceeds 4096 chars
   ├─ TelegramNotifier.cs        POST to sendMessage
   ├─ AlertHistoryStore.cs       reads/writes symbol -> last-alerted timestamp
   └─ AppLog.cs                  console + daily file in Logs/
```

Binance calls per run: **1** if nothing clears the lower threshold (the usual case). If
something does, add 1 for `exchangeInfo`. That's the ceiling — there are no per-symbol calls.

No DI or Generic Host: it's a single-shot program and doesn't need either.
`global.json` pins SDK 9.0.317 because the machine defaults to a .NET 10 preview.

## Things to keep in mind

- **`www.binance.com/fapi/...`, not `fapi.binance.com`**: measured from a GitHub runner,
  `fapi.binance.com` → **451**, `dapi.binance.com` → 451, `api.binance.com` → 451,
  `fapi1/2/3.binance.com` → 202 with an empty body (a block page), but
  **`www.binance.com/fapi/v1/...` → 200** and serves the full futures API. That single
  host is what makes running on GitHub Actions possible; without it the runner can't
  reach futures at all.
- **Futures `exchangeInfo` takes no `symbols` filter.** It always returns the full
  catalog (~1 MB), so it's fetched only once at least one symbol clears the lower of the
  two thresholds. In the usual quiet run the bot makes exactly **one** Binance call.
- Binance returns **every numeric field as a string**; parsing uses
  `CultureInfo.InvariantCulture` (see `CoinFilter`).
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
  the run exits with code `1` and doesn't save state, so the next run retries — which
  resends to chat 1 too. That's a deliberate trade-off: a rare duplicate is safer than
  silently never reaching a configured destination.
- The message uses `parse_mode: HTML`, so all dynamic text goes through the escaping
  in `MessageFormatter.Escape` (`&`, `<`, `>`).
- The token is part of the Telegram URL: it must never show up in logs or exceptions.
- Prices range from the tens of thousands down to 0.00000001, which is why
  `FormatPrice` switches format based on magnitude instead of using a fixed number of decimals.
