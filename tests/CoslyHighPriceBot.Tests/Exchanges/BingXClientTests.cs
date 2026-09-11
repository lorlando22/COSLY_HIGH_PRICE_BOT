using CoslyHighPriceBot.Configuration;
using CoslyHighPriceBot.Models;
using CoslyHighPriceBot.Services.Exchanges;

namespace CoslyHighPriceBot.Tests.Exchanges;

public class BingXClientTests
{
    private static readonly BingXOptions Options = new()
    {
        TickerUrl = "https://open-api.bingx.com/openApi/swap/v2/quote/ticker",
        ContractsUrl = "https://open-api.bingx.com/openApi/swap/v2/quote/contracts"
    };

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static BingXClient CreateClient(params (string UrlContains, string Body)[] responses)
    {
        var http = FakeHttpMessageHandler.CreateClient(responses);
        return new BingXClient(new ExchangeHttp(http, "BingX"), Options);
    }

    [Fact]
    public async Task GetTickersAsync_reads_the_percentage_directly()
    {
        var client = CreateClient(("quote/ticker", Fixture("bingx_ticker.json")));

        var tickers = await client.GetTickersAsync(CancellationToken.None);

        var btc = tickers.Single(t => t.Symbol == "BTC-USDT");
        Assert.Equal(0.36m, btc.ChangePercent);
        Assert.Null(btc.TradeCount); // BingX's ticker never reports a trade count.
    }

    [Fact]
    public async Task GetInstrumentsAsync_ignores_apiStateOpen_and_uses_status_instead()
    {
        var client = CreateClient(("quote/contracts", Fixture("bingx_contracts.json")));

        var instruments = await client.GetInstrumentsAsync(CancellationToken.None);

        // POWER-USDT has apiStateOpen=false in the real catalog but status=1 (ONLINE) and
        // trades normally from the app; it must still come out tradeable.
        var power = instruments["POWER-USDT"];
        Assert.True(power.Tradeable);
        Assert.Equal(CoinKind.Crypto, power.Kind);
    }

    [Fact]
    public async Task GetInstrumentsAsync_discards_the_wrong_currency_and_the_wrong_status()
    {
        var client = CreateClient(("quote/contracts", Fixture("bingx_contracts.json")));

        var instruments = await client.GetInstrumentsAsync(CancellationToken.None);

        var usdc = instruments["BTC-USDC"];
        Assert.False(usdc.Tradeable);
        Assert.Contains("USDC", usdc.DiscardReason);

        // status 25 (FORBIDDEN_TO_OPEN).
        var forbidden = instruments["NCCOCOFFEE2USD-USDT"];
        Assert.False(forbidden.Tradeable);
    }

    [Fact]
    public async Task GetInstrumentsAsync_uses_displayName_when_it_disagrees_with_the_symbol()
    {
        var client = CreateClient(("quote/contracts", Fixture("bingx_contracts.json")));

        var instruments = await client.GetInstrumentsAsync(CancellationToken.None);

        var neiro = instruments["NEIROCTO-USDT"];
        Assert.Equal(CoinKind.Crypto, neiro.Kind);
        Assert.Equal(["NEIROCTO", "NEIRO"], neiro.Aliases);

        var monad = instruments["MONAD-USDT"];
        Assert.Equal(["MONAD", "MON"], monad.Aliases);

        var pepe = instruments["1000PEPE-USDT"];
        Assert.Equal(["PEPE"], pepe.Aliases); // symbol and displayName both normalize to the same alias, so it's only listed once.
        Assert.Equal(1000m, pepe.Multiplier);
    }

    [Fact]
    public async Task GetInstrumentsAsync_classifies_tradfi_symbols_by_prefix_and_extracts_the_right_ticker()
    {
        var client = CreateClient(("quote/contracts", Fixture("bingx_contracts.json")));

        var instruments = await client.GetInstrumentsAsync(CancellationToken.None);

        var tesla = instruments["NCSKTSLA2USD-USDT"];
        Assert.Equal(CoinKind.TokenizedStock, tesla.Kind);
        Assert.Equal(["TSLA"], tesla.Aliases);

        // A few contracts skip the "2USD" marker and end in the quote instead.
        var sitime = instruments["NCSKSITMUSDT-USDT"];
        Assert.Equal(CoinKind.TokenizedStock, sitime.Kind);
        Assert.Equal(["SITM"], sitime.Aliases);

        var sp500 = instruments["NCSISP5002USD-USDT"];
        Assert.Equal(CoinKind.TokenizedStock, sp500.Kind);
        Assert.Equal(["SP500"], sp500.Aliases);

        var eurusd = instruments["NCFXEUR2USD-USDT"];
        Assert.Equal(CoinKind.TokenizedStock, eurusd.Kind);
        Assert.Contains("EURUSD", eurusd.Aliases);

        // Gold is the one commodity that carries a parenthetical ticker in its display name:
        // "GOLD(XAU)-USDT" -> both "GOLD" (from the symbol) and "XAU" (from the parentheses,
        // which is what lets it line up with Bybit's plain XAUUSDT).
        var gold = instruments["NCCOGOLD2USD-USDT"];
        Assert.Equal(CoinKind.TokenizedStock, gold.Kind);
        Assert.Equal(["GOLD", "XAU"], gold.Aliases);
    }

    [Fact]
    public async Task GetInstrumentsAsync_keeps_the_display_name_only_when_it_differs_from_the_symbol()
    {
        var client = CreateClient(("quote/contracts", Fixture("bingx_contracts.json")));

        var instruments = await client.GetInstrumentsAsync(CancellationToken.None);

        Assert.Equal("TSLA-USDT", instruments["NCSKTSLA2USD-USDT"].DisplaySymbol);
        Assert.Equal("MON-USDT", instruments["MONAD-USDT"].DisplaySymbol);
        Assert.Null(instruments["BTC-USDT"].DisplaySymbol);
    }

    [Fact]
    public async Task GetInstrumentsAsync_does_not_merge_the_price_collision_meme_symbols()
    {
        var client = CreateClient(("quote/contracts", Fixture("bingx_contracts.json")));

        var instruments = await client.GetInstrumentsAsync(CancellationToken.None);

        var meme = instruments["MEME-USDT"];
        Assert.Equal(["MEME"], meme.Aliases);

        // AMEMECOIN shares the "MEME" alias via its displayName, but PumpAggregator's price
        // check (tested separately) is what actually keeps it from merging with the real coin.
        var impostor = instruments["AMEMECOIN-USDT"];
        Assert.Equal(["AMEMECOIN", "MEME"], impostor.Aliases);
    }
}
