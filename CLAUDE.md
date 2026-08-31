# COSLY_HIGH_PRICE_BOT

.NET 9 console app that detects "pumps" on Binance **USD-M futures** and sends formatted
alerts to Telegram: symbols whose 24-hour change cleared a threshold, crypto and tokenized
stocks each with their own threshold, message and memory.

Code lives in `Modules/DailyPumpModule.cs`. It reports a move that has already happened —
by the time a coin reads +100%, the move is over — which is a known, accepted trade-off:
this bot answers "what has already moved a lot today?", not "what looks like it's starting
right now?".

**A sibling repo, `COSLY_EARLY_PUMP_BOT`, answers that second question.** It used to be a
second module in this same project, sharing only the ticker download with the 24h
detector — separate thresholds, messages, Telegram channels, state files and cooldowns.
It was split into its own solution because the two are different products that don't need
to be deployed together. This repo's git history up to the split still has that module's
code, in case it's useful as a reference (see commit `5b193be` and nearby).

If nothing exceeds the threshold, **nothing is sent** — it's just logged to the console.

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

## Latency and the scan loop

A run can either scan once and exit or **keep scanning** every `Run:IntervalSeconds` (60)
for up to `Run:MaxRunMinutes`, then exit and let the scheduler start the next one.
`Run:MaxRunMinutes = 0` picks the single pass.

**In the cloud it is set to `0`: one pass per run, and the 10-minute cron is the scan
cadence.** The loop existed for the early-pump module, which needed to catch a 5-minute
candle within a minute of it forming; that module now lives in its own repo. A detector
whose input is a 24-hour percentage gains nothing from re-reading it sixty seconds later,
and a single pass costs ~1 billable minute against the loop's ~14.

The loop is still there because a machine that stays on has no cron to lean on: that is
what `run-bot.cmd` uses, widening the window to a full day with `Run__MaxRunMinutes=1435`
so a repeating Task Scheduler trigger acts as a watchdog.

## Log

Everything printed to the console is also written to `Logs\pumps-<yyyy-MM-dd>.log`,
next to the executable: one file per day, one `yyyy-MM-dd HH:mm:ss [LEVEL] message`
line per entry.

Events that get logged: the start and end of each run (with its exit code), every
symbol notified via Telegram (saying whether it's crypto or a tokenized stock), every
symbol that drops below its threshold and is removed from its JSON, pairs discarded for
being suspended, and any exception.

Because a run scans a dozen times, **bookkeeping lines are logged once, events every
time.** Universe sizes, cooldown notes and "already notified" lists appear on the first
scan only; the classification lines reappear whenever the candidate set changes.

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

A symbol's entry survives while **either** of these holds, and only disappears when
both stop being true:

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
would rewrite the file on every run — which in the cloud means a git commit every 10
minutes.

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

By default (`Run:MaxRunMinutes = 0`) it scans once and exits. Set `Run__MaxRunMinutes` to
a positive number to make it keep scanning every `Run:IntervalSeconds` instead. Exit
codes: `0` success (whether or not any symbol matched), `1` error.

Useful overrides while working on it — everything is an environment variable:

```bash
# One pass, exactly how the bot behaves with the loop turned off.
Run__IntervalSeconds=0 dotnet run --project src/CoslyHighPriceBot

# Force alerts to check the plumbing end to end (send this somewhere harmless).
Run__IntervalSeconds=0 Filter__MinChangePercent=3 Filter__StockMinChangePercent=1 dotnet run --project src/CoslyHighPriceBot
```

## Publishing for Task Scheduler

```bash
publish.cmd
```

Produces `publish\CoslyHighPriceBot.exe` (a single file, ~575 KB) next to its
`appsettings.json`. It's *framework-dependent*: it needs
the .NET 9 runtime on the machine. To make it runtime-independent, add
`--self-contained true` to the script.

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
| `Run:IntervalSeconds` | Seconds between scans when the loop is on. |
| `Run:MaxRunMinutes` | How long a run keeps scanning. `0` = one scan and exit (the cloud default). Must stay below the cron interval. |
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

[`.github/workflows/daily-pump-alert.yml`](.github/workflows/daily-pump-alert.yml) runs
the bot every 10 minutes without depending on any machine being on.

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

**Cost.** A run is a single pass and bills ~1-2 minutes: roughly 6,500 minutes a month at
6 runs an hour. That is comfortably free on a public repo and, unlike the old 13-minute
scanning loop (~40,000 minutes a month), survivable on a private one.

`Run:MaxRunMinutes` **must stay below the cron interval.** At the old value of 13 against
a 10-minute cron, runs would overlap; the `concurrency` group would queue them rather than
run them in parallel, and the backlog would grow indefinitely. `0` sidesteps the question.

GitHub delays scheduled crons under load, so the actual interval can run noticeably
longer than 10 minutes. That only costs latency, never a missed pump: the state file
remembers what has already been announced, so a late run reports the same coin exactly
once.

## Structure

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

**Binance calls per scan:** 1, or 2 when something clears the threshold (the second is
`exchangeInfo`, cached after its first fetch each run).

No DI or Generic Host: one module constructed by hand in `Program.cs` doesn't need
either. `global.json` pins SDK 9.0.317 because the machine defaults to a .NET 10 preview.

## Things to keep in mind

- **`www.binance.com/fapi/...`, not `fapi.binance.com`**: measured from a GitHub runner,
  `fapi.binance.com` → **451**, `dapi.binance.com` → 451, `api.binance.com` → 451,
  `fapi1/2/3.binance.com` → 202 with an empty body (a block page), but
  **`www.binance.com/fapi/v1/...` → 200** and serves the full futures API. That single
  host is what makes running on GitHub Actions possible; without it the runner can't
  reach futures at all.
- **Futures `exchangeInfo` takes no `symbols` filter.** It always returns the full
  catalog (~1 MB), which is why `SymbolMetadataCache` fetches it **once per run** and
  shares it. Because it's lazy, a quiet run still costs exactly one Binance call.
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
  nothing is saved, so the next scan retries — which resends to chat 1 too. That's a
  deliberate trade-off: a rare duplicate is safer than silently never reaching a
  configured destination.
- The message uses `parse_mode: HTML`, so all dynamic text goes through the escaping
  in `MessageFormatter.Escape` (`&`, `<`, `>`).
- The token is part of the Telegram URL: it must never show up in logs or exceptions.
- Prices range from the tens of thousands down to 0.00000001, which is why
  `FormatPrice` switches format based on magnitude instead of using a fixed number of decimals.

## See also

`COSLY_EARLY_PUMP_BOT` — the sibling early-pump detector this bot's second module was
split out into.
