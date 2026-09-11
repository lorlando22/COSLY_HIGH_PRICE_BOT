# COSLY High Price Bot

A .NET 10 console app that watches **Binance, Bybit and BingX USD-M/USDT-M futures** for
pumps and alerts on Telegram. It detects coins whose 24-hour change cleared a threshold,
crypto and tokenized stocks each with their own threshold, message and state file. A coin
listed on more than one exchange produces one alert, not one per exchange — see "Multiple
exchanges" below.

| Detector | Question | Default | Message | State file |
| --- | --- | --- | --- | --- |
| **24h pump** — crypto | What already moved a lot today? | +100% in 24h, then every +50% | 🚀 Crypto Pumps | `notified-symbols.json` |
| **24h pump** — tokenized stocks | Same, on a scale equities actually reach | +20% in 24h | 📈 Tokenized Stocks | `notified-stocks.json` |

If nothing crosses its threshold, **no message is sent** — the run just logs that
nothing matched and exits cleanly. A coin is announced once and then held quiet: while
it stays above its threshold on at least one exchange, and for a configurable cooldown
(8h by default). That cooldown is what stops a coin that dips and re-crosses the
threshold minutes later from being announced two or three times.

A crypto coin that keeps climbing isn't held quiet forever, though: it gets re-announced
every `Filter:CryptoStepPercent` (+150%, +200%, +250%, ...) above its threshold, once per
milestone reached — so a coin that runs from +100% to +400% in a day produces a handful of
messages tracking the climb instead of just the first one. Tokenized stocks don't step;
a +15% day is already exceptional for them, so a single alert per pump is enough.

No server, no database. A run scans once and exits, so it fits a GitHub Actions cron (how
it's deployed) or a Windows Task Scheduler job. Set `Run:MaxRunMinutes` to a positive
number and it keeps scanning instead, for a machine that stays on and has no cron.

> **Looking for a pump detector that catches the move as it starts, instead of after a
> 24h threshold trips?** That's [`COSLY_EARLY_PUMP_BOT`](../COSLY_EARLY_PUMP_BOT), a
> sibling repo. It used to be a second module in this project; it was split out because
> the two are different products — different thresholds, messages, state and cooldowns
> — sharing only the idea of reading Binance and alerting on Telegram.

## Features

- Reads Binance, Bybit and BingX in parallel and merges the same coin listed on more than
  one exchange into a single alert, showing the % on every exchange it's tradeable on
- Separates tokenized equities (`TSLAUSDT`, `MRNAUSDT`, `HOODUSDT`, Bybit's `AMDSTOCK`,
  BingX's `NCSKTSLA2USD`...) from crypto using each exchange's own classification field,
  and applies a much lower threshold to them, since a +15% day for a stock is exceptional
- Lines a crypto coin's name up across exchanges even when a "large supply" multiplier
  changes its symbol (`1000PEPE`, `SHIB1000`) or one exchange's internal name doesn't
  match its real ticker (BingX's `NEIROCTO` vs `NEIRO`) — while still telling apart two
  unrelated tickers that happen to share a name by their price
- Skips symbols that aren't actually tradeable: suspended trading on Binance
  (`BREAK`/`HALT`), the wrong settlement currency, a dated future instead of a
  perpetual, or a catalog status/tier that means the same thing
- Sends a single alert per coin — no repeats while it stays above its threshold on any
  exchange, plus a cooldown (8h by default) so a dip-and-recross doesn't re-announce it
- Crypto that keeps climbing gets re-announced every `Filter:CryptoStepPercent` above
  its threshold (+100%, +150%, +200%, ...), once per milestone — tokenized stocks don't
  step and keep a single alert per pump
- Keeps going when one exchange fails: the rest still get checked, and a coin's memory
  isn't pruned on a scan where data might be missing rather than genuinely gone
- Optional scan loop for long-running local sessions, off by default in the cloud where
  the cron already provides the cadence
- Daily rotating log file with automatic retention cleanup
- Everything configurable via `appsettings.json` or environment variables (for secrets)
- Ready-to-use GitHub Actions workflow to run every 10 minutes for free

## Quick start

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

1. Copy `src/CoslyHighPriceBot/appsettings.ci.json` to
   `src/CoslyHighPriceBot/appsettings.json` and fill in your Telegram bot token and chat ID.
2. Run it:

   ```bash
   dotnet run --project src/CoslyHighPriceBot
   ```

It scans once, alerts on anything worth alerting about, and exits. Add
`Run__MaxRunMinutes=30` to make it keep scanning every 60 seconds for half an hour
instead. Exit code `0` on success (whether or not anything matched), `1` on error.

### Getting a Telegram bot token and chat ID

1. Talk to [@BotFather](https://t.me/BotFather) on Telegram, run `/newbot`, and copy
   the token it gives you.
2. For a private chat: send your bot any message, then open
   `https://api.telegram.org/bot<TOKEN>/getUpdates` and read `message.chat.id`.
3. For a group: add the bot to the group, send `/start@<bot>` there, and read
   `message.chat.id` from the same URL. Group IDs are **negative**.

## Multiple exchanges

Binance, Bybit and BingX are queried every scan, in parallel. `Exchanges:Priority`
(`Binance,Bybit,BingX` by default) decides, for a coin listed on more than one, whose
price/open/high/low/volume the message shows and whose name the coin's state-file key
gets the first time it's seen. A ticker or catalog fetch that fails for one exchange just
excludes it from that scan — the others keep going, and the run only fails if every
enabled exchange failed.

Grouping the same coin across exchanges relies on a shared name (checked against every
alias an exchange gives a coin, since BingX's internal symbol and its real ticker can
differ) and a compatible price (within 1.25x once each exchange's own "large supply"
multiplier, like `1000PEPE`, is normalized out) — the price check is what stops two
unrelated coins that happen to share a ticker from being merged.

**Bybit is currently disabled in the cloud config** (`appsettings.ci.json`): it's
geo-blocked (HTTP 403) from GitHub-hosted runners. It works locally and is implemented in
full — see [CLAUDE.md](CLAUDE.md) for the details.

## How tokenized stocks are detected

Each exchange tags every symbol with its own classification field, and that's what does
the whole job — no catalog to maintain and no name-pattern guessing, so a newly listed
equity is classified correctly the moment it appears:

```
Binance  "contractType": "TRADIFI_PERPETUAL"  -> tokenized stock   (~175 symbols)
Binance  "contractType": "PERPETUAL"          -> crypto            (~698 symbols)
Bybit    "symbolType": "stock" / "ETF" / "commodity" / "forex"  -> tokenized stock
Bybit    "symbolType": "" / "innovation"                        -> crypto
BingX    symbol prefix NCSK / NCSI / NCCO / NCFX                -> tokenized stock
BingX    anything else                                          -> crypto
```

This is the main reason the bot reads futures instead of spot. Spot exposes no such
field on any of the three exchanges — on Binance, the only hint is a `B` suffix on the
base asset (`AAPLB`), which also matches BNB, SHIB and ARB. Spot also simply doesn't
list many of the symbols worth watching: `BTRUSDT` pumped 250% as a futures-only listing.

## Configuration

Every adjustable value lives in `appsettings.json`:

| Key | Description |
| --- | --- |
| `Exchanges:Priority` | Comma-separated, highest priority first (default `Binance,Bybit,BingX`). Whose numbers a message shows and whose name a coin's state-file key gets when it's new. |
| `Binance:Enabled` | Turn Binance off (default `true`). At least one exchange must stay enabled. |
| `Binance:Ticker24hUrl` | Futures 24h ticker. With no query string it returns every symbol. |
| `Binance:ExchangeInfoUrl` | Symbol status and `contractType` (which classifies each symbol). |
| `Binance:QuoteAsset` | Quote asset to filter by (symbol suffix), e.g. `USDT`. |
| `Binance:OnlyTradingSymbols` | Discards suspended pairs (recommended: `true`). |
| `Bybit:Enabled` | Turn Bybit off (default `true`; `false` in the cloud config — see "Multiple exchanges"). |
| `Bybit:TickersUrl` | Linear 24h ticker for every symbol. |
| `Bybit:InstrumentsUrl` | Instrument catalog: status, contract type, settlement coin, symbol tier. |
| `BingX:Enabled` | Turn BingX off (default `true`). |
| `BingX:TickerUrl` | USDT-margined perpetuals 24h ticker. |
| `BingX:ContractsUrl` | Contract catalog: currency, status, display name. |
| `Filter:MinChangePercent` | Minimum 24h gain, in %, for crypto. |
| `Filter:StockMinChangePercent` | Minimum 24h gain, in %, for tokenized stocks. |
| `Filter:CooldownHours` | Hours before the same symbol can be alerted again. `0` disables it. |
| `Filter:CryptoStepPercent` | Extra gain (in %) between milestone alerts for **crypto**, on top of `MinChangePercent`. `0` = a single alert per pump. Tokenized stocks never step. |
| `Run:IntervalSeconds` | Seconds between scans when the loop is on. Default `60`. |
| `Run:MaxRunMinutes` | How long a run keeps scanning. `0` (the default) = one scan and exit. |
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

The included [`.github/workflows/daily-pump-alert.yml`](.github/workflows/daily-pump-alert.yml)
runs the bot every 10 minutes without needing any machine to stay on.

1. Fork or push this repo to GitHub — **keep it public** so Actions minutes are free
   (4,320 runs/month exceeds the private-repo free tier).
2. Add two repository secrets (Settings → Secrets and variables → Actions):
   `TELEGRAM_BOT_TOKEN` and `TELEGRAM_CHAT_IDS`.
3. Trigger it once manually from the Actions tab to confirm it works, or just wait
   for the next scheduled run.

The bot's memory (the two files under `state/`) is committed back to the repo after each
run, since GitHub Actions runners are ephemeral and don't persist disk state between runs
on their own. Each run's log is also uploaded as a downloadable artifact (90-day
retention), particularly useful when a run fails.

> **Cost.** Each run is a single pass and bills 1-2 minutes — roughly 6,500 minutes a
> month. Free on a public repo, and modest enough to be workable on a private one.
> If you raise `Run:MaxRunMinutes` above `0`, keep it **below the cron interval**, or
> runs overlap and the `concurrency` group queues an ever-growing backlog.

> **Why `www.binance.com/fapi/...` and not `fapi.binance.com`?** Binance blocks US
> datacenter IPs, which is where GitHub Actions runners live. Measured from a runner:
> `fapi.binance.com` → **451**, `api.binance.com` → 451, `fapi1/2/3.binance.com` → 202
> with an empty body, but **`www.binance.com/fapi/v1/...` → 200** and serves the full
> futures API. That host is what makes free CI hosting workable.
>
> **Why is Bybit disabled in the cloud config?** `api.bybit.com`, the alternate host
> `api.bytick.com` and every regional host (`.nl`, `.eu`, `.kz`, `byhkbit.com`...) return
> **403** from a GitHub runner — a CloudFront country block, different from Binance's
> datacenter-IP block. BingX has no such restriction. Bybit
> works fine outside GitHub Actions (locally, or on a self-hosted runner in an unblocked
> region); just flip `Bybit:Enabled` back to `true`.

## Deploying with Windows Task Scheduler

```bash
publish.cmd
```

This produces a single-file `publish\CoslyHighPriceBot.exe` (~575 KB, framework-dependent
— needs the .NET 10 runtime installed). Point a Task Scheduler action at that `.exe` on
whatever interval you like; the working directory doesn't matter, since configuration
is always read from the executable's own folder.

## Project structure

```
src/CoslyHighPriceBot/
├─ Program.cs                    bootstrap + scan loop + cross-exchange orchestration
├─ Configuration/AppSettings.cs  appsettings.json POCOs + validation
├─ Modules/
│  └─ DailyPumpModule.cs         the detector: crypto and tokenized stocks
├─ Models/
│  ├─ MarketTicker.cs            one exchange's parsed 24h snapshot for one symbol
│  ├─ InstrumentInfo.cs          CoinKind + what a catalog says about a symbol
│  ├─ ExchangeQuote.cs           a MarketTicker joined with its InstrumentInfo
│  └─ CoinGroup.cs               the same coin across every exchange that lists it
└─ Services/
   ├─ Exchanges/                 one IExchangeClient per exchange (Binance, Bybit, BingX)
   ├─ SymbolNormalizer.cs        strips a crypto coin's "large supply" multiplier
   ├─ PumpAggregator.cs          groups quotes into cross-exchange CoinGroups
   ├─ CoinFilter.cs              milestone math + shared invariant-culture parsing
   ├─ MessageFormatter.cs        HTML message text, split if it exceeds 4096 chars
   ├─ TelegramNotifier.cs        POST to sendMessage, per-chat send
   ├─ AlertHistoryStore.cs       reads/writes coin name -> last-alerted timestamp + milestone
   └─ AppLog.cs                  console + daily file in Logs/
```

No dependency injection or generic host — one module constructed by hand doesn't need
either. `global.json` pins the SDK version. `tests/CoslyHighPriceBot.Tests` (xUnit,
`dotnet test`) covers the exchange clients, the grouping logic and the alert-history
memory; see [CLAUDE.md](CLAUDE.md) for details.

## Notes on the exchanges' APIs

- Every numeric field comes back as a string on all three exchanges; parsing uses
  `CultureInfo.InvariantCulture` throughout.
- Symbols not in Binance's `TRADING` status (or Bybit's `Trading`, or BingX's status `1`)
  keep their 24h stats frozen, so they can look like enormous pumps that are actually
  untradeable. Binance's check is optional (`OnlyTradingSymbols`); Bybit and BingX always
  filter for it.
- Each exchange's catalog endpoint (Binance's `exchangeInfo`, Bybit's
  `instruments-info`, BingX's `contracts`) accepts no per-symbol filter and always
  returns everything, so each is fetched lazily and reused for an hour (in practice once
  per run in the cloud). A quiet run still costs exactly one ticker call per exchange and
  no catalog call at all.
- BingX's `apiStateOpen` only means "can a position be opened through the API right
  now" — several contracts with real trading volume have it `false` while trading
  normally from the app. `status` is the field that actually reflects whether a
  contract is listed and open.

## License

No license file yet — all rights reserved by default until one is added. If you'd
like to use or fork this project, consider opening an issue to ask about adding an
open-source license (MIT is a common, permissive choice).
