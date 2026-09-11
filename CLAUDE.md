# COSLY_HIGH_PRICE_BOT

.NET 10 console app that detects "pumps" across **Binance, Bybit and BingX USD-M/USDT-M
futures** and sends formatted alerts to Telegram: coins whose 24-hour change cleared a
threshold, crypto and tokenized stocks each with their own threshold, message and memory.
A coin listed on more than one exchange is one alert, not one per exchange — see
"Multiple exchanges" below. Crypto that keeps climbing past its threshold gets
re-announced every `Filter:CryptoStepPercent` (+150%, +200%, ...); a tokenized stock
always gets a single alert per pump, exactly like today.

Code lives in `Modules/DailyPumpModule.cs`, fed by `Services/PumpAggregator.cs` (the
cross-exchange grouping) and one `Services/Exchanges/*Client.cs` per exchange. It reports a
move that has already happened — by the time a coin reads +100%, the move is over — which
is a known, accepted trade-off: this bot answers "what has already moved a lot today?",
not "what looks like it's starting right now?".

**A sibling repo, `COSLY_EARLY_PUMP_BOT`, answers that second question.** It used to be a
second module in this same project, sharing only the ticker download with the 24h
detector — separate thresholds, messages, Telegram channels, state files and cooldowns.
It was split into its own solution because the two are different products that don't need
to be deployed together. This repo's git history up to the split still has that module's
code, in case it's useful as a reference (see commit `5b193be` and nearby).

If nothing exceeds the threshold, **nothing is sent** — it's just logged to the console.

## Multiple exchanges

Binance, Bybit and BingX are read in parallel every scan, each through its own
`IExchangeClient` (`Services/Exchanges/`). Only linear, USDT-settled perpetuals count on
every exchange — a dated future (Bybit's `BTCUSDT-25SEP26`) or a USDC-margined twin
(Bybit's `BTCPERP`, BingX's `BTC-USDC`) is discarded the same way a suspended Binance pair
is.

**The message** lists coins highest gain first. Each block names every exchange where the
coin is tradeable, in priority order, with its 24h change there and ✅ where it clears the
threshold; the symbol shown is the one the exchange's own app uses (BingX's `displayName`:
`TSLA-USDT`, not `NCSKTSLA2USD-USDT`). Price/open/high/low/volume come from the
highest-priority exchange that qualifies, and the trade count only appears when that
exchange reports one (Binance does, Bybit and BingX don't).

**Priority.** `Exchanges:Priority` (`Binance,Bybit,BingX` by default) decides two things
for a coin listed on more than one exchange: whose price/open/high/low/volume/trade-count
the message shows, and whose name becomes the coin's key in the state file the first time
it's seen. An enabled exchange missing from the list is used last.

**Grouping (`PumpAggregator`, static and pure).** Every tradeable, classified quote from
every exchange that responded — not just the ones above threshold, so the message can show
the % on the others too — is grouped by shared name (alias) and compatible price
(multiplier-adjusted price within a 1.25x band). The price check is what stops two
unrelated tickers that happen to share a name from merging: BingX's `MEME` (`AMEMECOIN`)
trades ~100x below Binance/Bybit's actual MEME coin, so despite the shared name they stay
in separate groups. Crypto and tokenized stocks are never grouped together. A coin can
bridge two otherwise-separate groups transitively — Binance's `NEIRO`, BingX's `NEIROCTO`
(whose `displayName` is `NEIRO`) and Bybit's `1000NEIROCTO` all end up in one group because
BingX's quote shares an alias with both of the others.

**Multiplier normalization (`SymbolNormalizer`).** A "large supply" prefix or suffix
(`1000PEPE`, `10000SATS`, `1MBABYDOGE`, Bybit's suffix form `SHIB1000`) is stripped from a
crypto name before it's used as an alias or compared by price, so 1000PEPE on Bybit/BingX
lines up with plain PEPE on Binance. Never applied to tokenized stocks, and never strips a
bare digit that isn't a full multiplier token (`1INCH`, `B2`, `LUNA2`, `API3`,
`BANANAS31` are untouched).

**Partial failure.** A ticker or catalog fetch that fails for one exchange excludes it from
that scan and logs the error; the rest continue. All enabled exchanges failing is the only
thing that fails the scan (exit 1). A partial failure still exits 0 (so the workflow commits
whatever the exchanges that did respond already sent) but **skips the pruning pass**
entirely for that scan: a coin's entry isn't forgotten just because the one exchange that
reports it happened to be unreachable — see "One alert per symbol" below.

**Bybit is geo-blocked from GitHub-hosted runners** (HTTP 403, `"The Amazon CloudFront
distribution is configured to block access from your country"`) as of this writing — see
"Things to keep in mind". It's implemented fully and enabled by default (`Bybit:Enabled =
true`), but `appsettings.ci.json` turns it off (`false`) so the cloud run doesn't spend a
scan failing to reach it. A disabled exchange doesn't count as a failure and is logged once,
at startup, instead of every scan.

## Per-exchange rules

All three: only linear perpetuals settled in USDT, operable/tradeable. A symbol missing
from its exchange's instrument catalog is discarded, same as Binance always worked.

**Binance** — unchanged. `contractType` classifies (`PERPETUAL` crypto, `TRADIFI_PERPETUAL`
tokenized stock); `status` must be `TRADING` when `Binance:OnlyTradingSymbols` is set; name
is `baseAsset`.

```
"contractType": "TRADIFI_PERPETUAL"   -> tokenized stock   (~175 symbols)
"contractType": "PERPETUAL"           -> crypto            (~698 symbols)
```

That field is the main reason this bot reads futures rather than spot. **Spot has no
equivalent**: every symbol looks alike there, and the only usable hint is a `B` suffix on
the base asset (`AAPLB`), which misclassifies BNB, SHIB and ARB. An earlier version kept
a hand-maintained catalog to work around that; futures made it unnecessary.

**Bybit** — operable requires `contractType == "LinearPerpetual"` **and**
`status == "Trading"` (explicit: the instruments endpoint also returns `PendingOpen` by
default) **and** `settleCoin == "USDT"` **and** `isPreListing == false`. Instruments are
**paginated** (`nextPageCursor`; today's ~870 contracts fit one page at `limit=1000`, but
the default is 500 and the catalog grows). `symbolType` classifies: `""` or `"innovation"`
(a risk tier, not a TradFi marker) → crypto; `"stock"`, `"ETF"`, `"commodity"`, `"forex"` →
tokenized stock; anything else is an unrecognized tier and is **discarded and logged**
rather than guessed at. Ticker's `price24hPcnt` is a **fraction** (0.047644 = +4.76%),
multiplied by 100 to match the other exchanges. Crypto name is `baseCoin`; stock name is
`baseCoin` with a trailing `STOCK` trimmed (`AMDSTOCK` → `AMD`) plus `underlyingTicker`
when it's letters-only and different.

**BingX** — operable requires `currency == "USDT"` **and** `status == 1` (ONLINE). **Never
use `apiStateOpen`**: it only says whether a position can be opened *through the API*, and
contracts with real volume (`POWER`, `MAGMA`, `SPORTFUN`) have `apiStateOpen=false` while
trading normally from the app. Status codes: `0` OFFLINE, `1` ONLINE, `5` PRE_ONLINE, `25`
FORBIDDEN_TO_OPEN (and those don't even appear in the ticker). Tokenized stocks are
identified by a symbol prefix, since there's no dedicated field: `NCSK` stocks, `NCSI`
indices/ETFs, `NCCO` commodities, `NCFX` forex; everything else is crypto. Crypto name is
the base of the `symbol` **and** the base of `displayName` when they differ — BingX uses an
internal symbol that doesn't match the real ticker for close to 30 coins (`MONAD` → `MON`,
`LIGHTER` → `LIT`, `OPENLEDGER` → `OPEN`, `NEIROCTO` → `NEIRO`). Stock name is the ticker
extracted from `NC..<TICKER>2USD-USDT` (`NCSKTSLA2USD` → `TSLA`), the base of
`displayName`, and — for `NCCO` only — the content inside the display name's parentheses
(`GOLD(XAU)` → `XAU`, which is what lets it line up with Bybit's plain `XAUUSDT`).
`status` is a JSON number while `apiStateOpen` is a string; every numeric field on both
Bybit and BingX comes back as a string, like Binance.

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

Events that get logged: the start and end of each run (with its exit code), every coin
notified via Telegram (saying whether it's crypto or a tokenized stock), every coin that
drops below its threshold and is removed from its JSON, symbols discarded per exchange
(suspended, wrong settlement currency, wrong status, unrecognized catalog tier — each
line names the exchange and the reason), and any exception.

Because a run scans a dozen times, **bookkeeping lines are logged once, events every
time.** Universe sizes, cooldown notes and "already notified" lists appear on the first
scan only; the classification lines reappear whenever the candidate set changes.

If the folder can't be written to, file logging turns itself off and the program keeps
going on the console: failing to log can never be allowed to block a pump alert.

On startup, logs older than `Logging:RetentionDays` are deleted (30 by default, `0`
keeps them all). Age comes from **the date in the file name**, not its modification
date, so copying the folder doesn't make old logs look fresh. Files that don't match
the `pumps-<yyyy-MM-dd>.log` pattern are left untouched.

## One alert per symbol (per milestone, for crypto)

Each state file maps a coin's name — not an exchange symbol, since the same coin can trade
under a different symbol on each exchange — to **when its Telegram message went out and
the milestone it reported**:

```json
{ "HEMI": { "notifiedAt": "2026-08-21T13:22:04+00:00", "milestone": 250 } }
```

**Looked up by alias.** A cross-exchange group (see "Multiple exchanges") is looked up in
the history by *every* alias it has, not just its current primary name — the exchange that
was highest priority when a coin was first notified about might not be the one still
listing it, or highest priority, today. When more than one alias matches (rare: it means
two legacy entries for the same coin), the one with the higher milestone wins. Whichever
key was found is the one updated on save, so the state file's key for a coin stays stable
even if its highest-priority exchange changes; only a coin seen for the first time gets a
fresh key (the group's own primary alias).

A known, accepted blind spot: the lookup has no price check, so two **different** coins that
share an alias share a memory. BingX's `AMEMECOIN` (displayed as `MEME`) is kept out of the
real MEME's *group* by price, but if both pump within one cooldown, whichever is announced
second is taken as already notified. It needs a name collision and two simultaneous pumps,
and silence over duplication is this file's rule anyway.

A coin's entry survives while **either** of these holds, and only disappears when
both stop being true:

- it's still above its threshold **on some exchange**, or
- its `Filter:CooldownHours` cooldown (8h by default) hasn't expired yet.

**A scan where any enabled exchange failed skips pruning entirely** — a coin missing from
this scan's groups might just mean its exchange couldn't be read, not that it actually fell
below threshold. See "Partial failure" above.

That governs when a symbol is forgotten, not when it's alerted about again: a **new**
milestone always sends, cooldown or not — the cooldown only exists to decide when a
symbol that fell back below threshold stops being remembered.

Two rules fall out of the "entry survives" part:

- **A sustained pump that doesn't clear the next milestone produces one message.** A
  crypto coin sitting at +120% (threshold 100, step 50 -> milestone 100) for three days is
  announced once, because its entry never leaves the file and 120 never clears 150.
  Tokenized stocks never step (see below), so for them this is simply "one message, ever,
  per pump."
- **Flapping produces one message.** A coin that crosses the threshold, dips, and crosses
  again minutes later stays in the file the whole time, so it isn't re-announced. This is
  the reason the cooldown exists: before it, the dip erased the memory and the second
  crossing counted as new, producing two or three messages for the same coin.

**Crypto milestones.** `Filter:CryptoStepPercent` (50 by default) makes a crypto coin that
keeps climbing get re-announced every step above the threshold: +100%, +150%, +200%, and
so on (`CoinFilter.Milestone`). `0` collapses this to the old behaviour, a single alert per
pump. Tokenized stocks always pass a step of `0` — they're a different product with a much
lower threshold, where a +15% day is already exceptional, so stepping was never wanted for
them. A coin seen for the **first time** already past a later milestone (e.g. +270% on a
first sighting) gets **one** message for the milestone it's actually at (250), not one for
every milestone it would have crossed on the way up.

**The timestamp is refreshed only when a message actually goes out for a new milestone** —
the first crossing of the threshold, or a later one clearing the next step. A pump that
keeps scanning without reaching the next milestone leaves the timestamp untouched, so once
it eventually drops below threshold the cooldown still counts from that last real event.
That keeps the number of rewrites — and, in the cloud, git commits — bounded by how many
milestones actually get hit, not by how many times the loop scans.

Saving happens **after** sending: if Telegram fails, nothing is recorded and the next scan
retries. Because each kind saves its own file right after its own send, a failure sending
one kind doesn't discard the other's progress. A corrupted file doesn't crash the program
— it's logged, ignored, and rewritten (the cost is a possible duplicate alert).

**Migration.** `AlertHistoryStore.Load` reads three shapes: a plain array of symbols (the
oldest format, no timestamp at all), an object mapping symbol to an ISO timestamp string
(the format before milestones existed), and the current `{ notifiedAt, milestone }` object.
Both older shapes are treated as "notified just now, milestone unknown" (`milestone: null`)
— silence over duplication is the rule, so on the next scan that entry silently adopts
whatever milestone the coin is currently at instead of guessing and possibly re-announcing
it. The file is rewritten in the current shape as soon as it's saved.

Separately, `AlertHistoryStore.MigrateLegacyKeys` handles the single-exchange era's key
shape: a key ending in the quote asset with more than that suffix (`HEMIUSDT`) is assumed
to be a Binance symbol and has the suffix stripped (`HEMI`); in the crypto file, the result
is also run through `SymbolNormalizer` (`1000PEPEUSDT` → `PEPE`), since state-file keys
follow the same alias rules as everything else now. A collision after migration (two
legacy keys landing on the same coin) keeps whichever entry has the higher milestone, the
more recent timestamp breaking a tie.

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
the .NET 10 runtime on the machine. To make it runtime-independent, add
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
| `Exchanges:Priority` | Comma-separated, highest priority first (`Binance,Bybit,BingX` by default). Whose numbers a message shows and whose name a coin's state-file key gets when it's new. An enabled exchange missing from the list is used last. |
| `Binance:Enabled` | Turn Binance off. Defaults to `true`; at least one exchange must stay enabled. |
| `Binance:Ticker24hUrl` | Futures 24h ticker. With no query string it returns every symbol. |
| `Binance:ExchangeInfoUrl` | Symbol status and `contractType` (which classifies each symbol). |
| `Binance:QuoteAsset` | Quote asset to filter by (symbol suffix). |
| `Binance:OnlyTradingSymbols` | Discards suspended pairs (see below). |
| `Bybit:Enabled` | Turn Bybit off. `true` in the POCO default, `false` in `appsettings.ci.json` (geo-blocked from GitHub runners — see "Multiple exchanges"). |
| `Bybit:TickersUrl` | Linear 24h ticker for every symbol. |
| `Bybit:InstrumentsUrl` | Instrument catalog (status, contract type, settle coin, symbol tier). Paginated. |
| `BingX:Enabled` | Turn BingX off. Defaults to `true`. |
| `BingX:TickerUrl` | USDT-margined perpetuals 24h ticker. |
| `BingX:ContractsUrl` | Contract catalog (currency, status, display name). |
| `Filter:MinChangePercent` | Minimum 24h gain, in %, for **crypto**. |
| `Filter:StockMinChangePercent` | Minimum 24h gain, in %, for **tokenized stocks**. |
| `Filter:CooldownHours` | Hours before the same symbol can be alerted again. `0` disables it. |
| `Filter:CryptoStepPercent` | Extra gain (in %) between milestone alerts for **crypto**, on top of `MinChangePercent`. `0` = a single alert per pump. Tokenized stocks never step. |
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
├─ Program.cs                    bootstrap + scan loop + cross-exchange orchestration
├─ Configuration/AppSettings.cs  appsettings.json POCOs + validation
├─ Modules/
│  └─ DailyPumpModule.cs         the detector: crypto and tokenized stocks
├─ Models/
│  ├─ MarketTicker.cs            one exchange's parsed 24h snapshot for one symbol
│  ├─ InstrumentInfo.cs          CoinKind + what a catalog says about a symbol (tradeable, kind, aliases, multiplier)
│  ├─ ExchangeQuote.cs           a MarketTicker joined with its InstrumentInfo, tagged with its exchange
│  └─ CoinGroup.cs               the same coin across every exchange that lists it
└─ Services/
   ├─ Exchanges/
   │  ├─ IExchangeClient.cs      Name, GetTickersAsync, GetInstrumentsAsync
   │  ├─ ExchangeHttp.cs         GET+JSON with 429/418/5xx backoff, shared by every client
   │  ├─ InstrumentCache.cs      fetches one exchange's catalog lazily, reuses it for an hour
   │  ├─ BinanceClient.cs        unchanged behaviour, now behind IExchangeClient
   │  ├─ BybitClient.cs          paginated catalog, fraction-to-percent ticker
   │  └─ BingXClient.cs          apiStateOpen ignored, symbol-prefix TradFi classification
   ├─ SymbolNormalizer.cs        strips a crypto coin's "large supply" multiplier
   ├─ PumpAggregator.cs          groups quotes into cross-exchange CoinGroups
   ├─ CoinFilter.cs              Milestone + shared invariant-culture parsing
   ├─ MessageFormatter.cs        HTML message text, split if it exceeds 4096 chars
   ├─ TelegramNotifier.cs        POST to sendMessage, per-chat send
   ├─ AlertHistoryStore.cs       reads/writes coin name -> last-alerted timestamp, alias-based lookup
   └─ AppLog.cs                  console + daily file in Logs/
```

**Calls per scan:** 1 per enabled exchange (the ticker), or up to 2 per enabled exchange
when something, anywhere, clears the lower of the two thresholds (the second is the
instrument catalog, cached after its first fetch each run) — 3 on a quiet scan with all
three enabled, up to 6 with a candidate.

No DI or Generic Host: one module constructed by hand in `Program.cs` doesn't need
either. `global.json` pins SDK 10.0.400 so a machine with several SDKs installed side by
side (this one has 6, 9 and 10) builds with the same one every time instead of whichever
`dotnet` happens to resolve to first.

## Tests

```bash
dotnet test
```

`tests/CoslyHighPriceBot.Tests` (xUnit) covers `SymbolNormalizer`, Bybit/BingX
classification against trimmed real-response fixtures (`tests/CoslyHighPriceBot.Tests/Fixtures`),
`PumpAggregator`'s grouping rules, and the alert-history alias lookup, pruning and
migration. It's referenced from `CoslyHighPriceBot.sln` but not from `publish.cmd` or the
workflow, both of which still build only `src/CoslyHighPriceBot`.

## Things to keep in mind

- **`www.binance.com/fapi/...`, not `fapi.binance.com`**: measured from a GitHub runner,
  `fapi.binance.com` → **451**, `dapi.binance.com` → 451, `api.binance.com` → 451,
  `fapi1/2/3.binance.com` → 202 with an empty body (a block page), but
  **`www.binance.com/fapi/v1/...` → 200** and serves the full futures API. That single
  host is what makes running on GitHub Actions possible; without it the runner can't
  reach futures at all.
- **`api.bybit.com` and `api.bytick.com` (the alternate host) both → 403** from a GitHub
  runner: `"The Amazon CloudFront distribution is configured to block access from your
  country"`, a country-level block rather than the datacenter-IP block Binance applies.
  Every regional host answers the same 403 from a runner (`api.bybitglobal.com`,
  `api.bybit.nl`, `api.byhkbit.com`, `api.bybit-tr.com`, `api.bybit.kz`,
  `api.bybitgeorgia.ge`, `api.bybit.ae`, `api.bybit.eu`, `api.bybit.id`, all measured
  2026-09-11) and `api2.bybit.com` doesn't resolve, so there is no host to switch to: Bybit
  in the cloud would need a proxy or a runner outside the US.
  That's why `appsettings.ci.json` sets `Bybit:Enabled = false` — see "Multiple
  exchanges". `open-api.bingx.com` (and `.io`) both → 200 from the same runner, no
  workaround needed.
- **Every exchange endpoint compresses well** — BingX's ticker drops from ~367 KB to
  ~78 KB, Binance's from ~284 KB to ~64 KB — which is why the shared `HttpClient` is
  built on a `SocketsHttpHandler` with `AutomaticDecompression = DecompressionMethods.All`.
- **Futures `exchangeInfo` (and Bybit's/BingX's equivalents) take no per-symbol filter.**
  Each returns its full catalog, which is why `InstrumentCache` fetches it **lazily and
  reuses it for an hour**, one instance per exchange. In the cloud's single-pass runs that
  means once per run; the hour only matters to `run-bot.cmd`'s day-long session, where a
  coin listed after startup would otherwise be missing from the catalog — and silently
  skipped — until the next day. A failed refresh keeps using the previous catalog. Because it's lazy, a quiet run still
  costs exactly one ticker call per exchange and no catalog call at all.
- Every exchange returns **every numeric field as a string** (Bybit's `isPreListing` and
  BingX's `status` are the two JSON-native exceptions); parsing uses
  `CultureInfo.InvariantCulture` (see `CoinFilter.TryParse`/`Parse`).
- **Suspended pairs**: Binance symbols in `BREAK` or `HALT` status keep their 24h stats
  frozen, so they show up as huge pumps that can't actually be traded. In a real test,
  4 of 9 coins above the threshold were in `BREAK`. That's why `OnlyTradingSymbols`
  defaults to `true`. Bybit and BingX always filter for tradeability themselves — there's
  no equivalent switch to turn off for them.
- **Watch out for collection defaults in configuration**: the
  `Microsoft.Extensions.Configuration` binder calls `Add()` on the property's existing
  list instead of replacing it, so a `List<T>` option must never be given a non-empty
  default — the JSON values get appended to it rather than replacing it. That's why
  `Exchanges:Priority` is a comma-separated string, not a `List<string>`.
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
