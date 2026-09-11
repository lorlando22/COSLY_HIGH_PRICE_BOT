using CoslyHighPriceBot.Models;
using CoslyHighPriceBot.Services;

namespace CoslyHighPriceBot.Tests;

public class PumpAggregatorTests
{
    private static readonly IReadOnlyList<string> Priority = ["Binance", "Bybit", "BingX"];

    private static ExchangeQuote Quote(
        string exchange, string symbol, CoinKind kind, decimal changePercent, decimal adjustedPrice, params string[] aliases) =>
        new(exchange, symbol, kind, changePercent, adjustedPrice, adjustedPrice, adjustedPrice, adjustedPrice,
            1_000_000m, exchange == "Binance" ? 12345 : null, adjustedPrice, aliases);

    [Fact]
    public void Group_merges_the_same_coin_quoted_on_every_exchange_into_one_group()
    {
        var quotes = new List<ExchangeQuote>
        {
            Quote("Binance", "1000PEPEUSDT", CoinKind.Crypto, 254.30m, 0.003306m, "PEPE"),
            Quote("Bybit", "1000PEPEUSDT", CoinKind.Crypto, 248.10m, 0.003306m, "PEPE"),
            Quote("BingX", "1000PEPE-USDT", CoinKind.Crypto, 96.00m, 0.003307m, "PEPE"),
        };

        var groups = PumpAggregator.Group(quotes, Priority);

        var group = Assert.Single(groups);
        Assert.Equal("PEPE", group.Key);
        Assert.Equal(3, group.Quotes.Count);
        // Priority order: Binance first, then Bybit, then BingX.
        Assert.Equal(["Binance", "Bybit", "BingX"], group.Quotes.Select(q => q.ExchangeName));
    }

    [Fact]
    public void Group_merges_transitively_when_a_bridging_alias_connects_two_separate_groups()
    {
        // Binance lists it as NEIRO, Bybit as 1000NEIROCTO (normalized to NEIROCTO), and
        // BingX's symbol is NEIROCTO but its displayName is NEIRO — so BingX's quote is the
        // only one that shares an alias with both of the other two, and should fold them
        // into a single group instead of leaving three.
        var quotes = new List<ExchangeQuote>
        {
            Quote("Binance", "NEIROUSDT", CoinKind.Crypto, 120m, 0.0000832m, "NEIRO"),
            Quote("Bybit", "1000NEIROCTOUSDT", CoinKind.Crypto, 118m, 0.0000834m, "NEIROCTO"),
            Quote("BingX", "NEIROCTO-USDT", CoinKind.Crypto, 121m, 0.0000833m, "NEIROCTO", "NEIRO"),
        };

        var groups = PumpAggregator.Group(quotes, Priority);

        var group = Assert.Single(groups);
        Assert.Equal("NEIRO", group.Key);
        Assert.Equal(3, group.Quotes.Count);
    }

    [Fact]
    public void Group_does_not_merge_two_coins_that_share_a_name_but_not_a_price()
    {
        // BingX's "MEME" (AMEMECOIN, displayName MEME-USDT) trades around 100x the price of
        // Binance/Bybit's actual MEME coin: same alias, incompatible price, must stay apart.
        var quotes = new List<ExchangeQuote>
        {
            Quote("Binance", "MEMEUSDT", CoinKind.Crypto, 15m, 0.0005168m, "MEME"),
            Quote("Bybit", "MEMEUSDT", CoinKind.Crypto, 14m, 0.0005179m, "MEME"),
            Quote("BingX", "AMEMECOIN-USDT", CoinKind.Crypto, 90m, 0.0494m, "AMEMECOIN", "MEME"),
        };

        var groups = PumpAggregator.Group(quotes, Priority);

        Assert.Equal(2, groups.Count);
        var real = groups.Single(g => g.Key == "MEME");
        Assert.Equal(2, real.Quotes.Count);
        var impostor = groups.Single(g => g.Key == "AMEMECOIN");
        Assert.Single(impostor.Quotes);
    }

    [Fact]
    public void Group_never_mixes_crypto_with_a_tokenized_stock_even_with_a_shared_alias_and_price()
    {
        var quotes = new List<ExchangeQuote>
        {
            Quote("Binance", "CATUSDT", CoinKind.Crypto, 110m, 5.00m, "CAT"),
            Quote("Bybit", "CATSTOCKUSDT", CoinKind.TokenizedStock, 25m, 5.00m, "CAT"),
        };

        var groups = PumpAggregator.Group(quotes, Priority);

        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, g => g.Kind == CoinKind.Crypto);
        Assert.Contains(groups, g => g.Kind == CoinKind.TokenizedStock);
    }

    [Fact]
    public void Group_never_puts_two_quotes_from_the_same_exchange_in_one_group()
    {
        // Two different Binance symbols that happen to share an alias and a compatible price
        // shouldn't be possible in practice (aliases come from a single instrument catalog
        // entry each), but the rule is still enforced defensively.
        var quotes = new List<ExchangeQuote>
        {
            Quote("Binance", "FOOUSDT", CoinKind.Crypto, 110m, 1.00m, "FOO"),
            Quote("Binance", "BARUSDT", CoinKind.Crypto, 90m, 1.01m, "FOO"),
        };

        var groups = PumpAggregator.Group(quotes, Priority);

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void BestQualifyingChangePercent_picks_the_highest_change_among_qualifying_quotes()
    {
        var quotes = new List<ExchangeQuote>
        {
            Quote("Binance", "1000PEPEUSDT", CoinKind.Crypto, 254.30m, 0.003306m, "PEPE"),
            Quote("Bybit", "1000PEPEUSDT", CoinKind.Crypto, 248.10m, 0.003306m, "PEPE"),
            Quote("BingX", "1000PEPE-USDT", CoinKind.Crypto, 96.00m, 0.003307m, "PEPE"),
        };

        var group = Assert.Single(PumpAggregator.Group(quotes, Priority));

        Assert.Equal(254.30m, group.BestQualifyingChangePercent(100m));
        Assert.Equal("Binance", group.BestQualifyingQuote(100m)!.ExchangeName);
        // Nothing clears a 300% threshold.
        Assert.Null(group.BestQualifyingChangePercent(300m));
    }
}
