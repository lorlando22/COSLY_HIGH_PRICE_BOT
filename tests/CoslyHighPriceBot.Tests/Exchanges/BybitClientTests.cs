using CoslyHighPriceBot.Configuration;
using CoslyHighPriceBot.Models;
using CoslyHighPriceBot.Services.Exchanges;

namespace CoslyHighPriceBot.Tests.Exchanges;

public class BybitClientTests
{
    private static readonly BybitOptions Options = new()
    {
        TickersUrl = "https://api.bybit.com/v5/market/tickers?category=linear",
        InstrumentsUrl = "https://api.bybit.com/v5/market/instruments-info?category=linear&limit=1000"
    };

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static BybitClient CreateClient(params (string UrlContains, string Body)[] responses)
    {
        var http = FakeHttpMessageHandler.CreateClient(responses);
        return new BybitClient(new ExchangeHttp(http, "Bybit"), Options);
    }

    [Fact]
    public async Task GetTickersAsync_converts_the_fraction_to_a_percentage()
    {
        var client = CreateClient(("tickers", Fixture("bybit_tickers.json")));

        var tickers = await client.GetTickersAsync(CancellationToken.None);

        var btc = tickers.Single(t => t.Symbol == "BTCUSDT");
        // price24hPcnt "0.003627" -> +0.3627%.
        Assert.Equal(0.3627m, btc.ChangePercent);
        Assert.Null(btc.TradeCount); // Bybit's ticker never reports a trade count.
    }

    [Fact]
    public async Task GetInstrumentsAsync_classifies_ordinary_and_multiplier_crypto()
    {
        var client = CreateClient(("instruments-info", Fixture("bybit_instruments.json")));

        var instruments = await client.GetInstrumentsAsync(CancellationToken.None);

        var btc = instruments["BTCUSDT"];
        Assert.True(btc.Tradeable);
        Assert.Equal(CoinKind.Crypto, btc.Kind);
        Assert.Equal(["BTC"], btc.Aliases);
        Assert.Equal(1m, btc.Multiplier);

        var pepe = instruments["1000PEPEUSDT"];
        Assert.True(pepe.Tradeable);
        Assert.Equal(CoinKind.Crypto, pepe.Kind);
        Assert.Equal(["PEPE"], pepe.Aliases);
        Assert.Equal(1000m, pepe.Multiplier);

        var shib = instruments["SHIB1000USDT"];
        Assert.Equal(["SHIB"], shib.Aliases);
        Assert.Equal(1000m, shib.Multiplier);

        // symbolType "innovation" is a risk tier, not a TradFi marker: still crypto.
        var acu = instruments["ACUUSDT"];
        Assert.True(acu.Tradeable);
        Assert.Equal(CoinKind.Crypto, acu.Kind);
    }

    [Fact]
    public async Task GetInstrumentsAsync_classifies_tokenized_stocks_and_their_aliases()
    {
        var client = CreateClient(("instruments-info", Fixture("bybit_instruments.json")));

        var instruments = await client.GetInstrumentsAsync(CancellationToken.None);

        var amd = instruments["AMDSTOCKUSDT"];
        Assert.True(amd.Tradeable);
        Assert.Equal(CoinKind.TokenizedStock, amd.Kind);
        Assert.Equal(["AMD"], amd.Aliases); // baseCoin "AMDSTOCK" trimmed and underlyingTicker "AMD" both resolve to the same alias.

        var xau = instruments["XAUUSDT"];
        Assert.Equal(CoinKind.TokenizedStock, xau.Kind);
        Assert.Equal(["XAU"], xau.Aliases); // commodity, no underlyingTicker.

        var eurusd = instruments["EURUSDUSDT"];
        Assert.Equal(CoinKind.TokenizedStock, eurusd.Kind); // forex.
    }

    [Theory]
    [InlineData("BTCUSDT-25SEP26")] // dated LinearFutures, not a perpetual.
    [InlineData("SHIB1000PERP")] // USDC-settled twin of a USDT symbol.
    [InlineData("NEWLISTUSDT")] // status PendingOpen, not Trading.
    [InlineData("PRELISTUSDT")] // isPreListing = true.
    [InlineData("REITXUSDT")] // symbolType Bybit hasn't documented yet.
    public async Task GetInstrumentsAsync_discards_everything_that_should_never_alert(string symbol)
    {
        var client = CreateClient(("instruments-info", Fixture("bybit_instruments.json")));

        var instruments = await client.GetInstrumentsAsync(CancellationToken.None);

        var instrument = instruments[symbol];
        Assert.False(instrument.Tradeable);
        Assert.Null(instrument.Kind);
        Assert.NotNull(instrument.DiscardReason);
    }
}
