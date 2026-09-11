using CoslyHighPriceBot.Configuration;
using CoslyHighPriceBot.Models;
using CoslyHighPriceBot.Modules;
using CoslyHighPriceBot.Services;
using CoslyHighPriceBot.Tests.Exchanges;

namespace CoslyHighPriceBot.Tests;

public class DailyPumpModuleTests : IDisposable
{
    private readonly string cryptoFile = Path.Combine(Path.GetTempPath(), $"cosly-test-crypto-{Guid.NewGuid():N}.json");
    private readonly string stockFile = Path.Combine(Path.GetTempPath(), $"cosly-test-stock-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        File.Delete(cryptoFile);
        File.Delete(stockFile);
    }

    private static AppSettings CreateSettings() => new()
    {
        Filter = new FilterOptions { MinChangePercent = 100m, StockMinChangePercent = 20m, CooldownHours = 8, CryptoStepPercent = 50m },
        Telegram = new TelegramOptions { ApiBaseUrl = "http://localhost", BotToken = "test-token", ChatIds = "1" },
    };

    private DailyPumpModule CreateModule(AppSettings settings, out List<string> sentMessages)
    {
        var captured = new List<string>();
        sentMessages = captured;
        var http = FakeHttpMessageHandler.CreateClient(captured, ("sendMessage", "{\"ok\":true}"));
        var telegram = new TelegramNotifier(http, settings.Telegram);
        return new DailyPumpModule(telegram, settings, new AlertHistoryStore(cryptoFile), new AlertHistoryStore(stockFile));
    }

    private static ExchangeQuote Quote(string exchange, string symbol, decimal changePercent, params string[] aliases) =>
        new(exchange, symbol, CoinKind.Crypto, changePercent, 1m, 1m, 1m, 1m, 1_000_000m, null, 1m, aliases);

    private static CoinGroup Group(string key, params ExchangeQuote[] quotes) => new(key, CoinKind.Crypto, quotes);

    [Fact]
    public async Task A_new_group_above_threshold_sends_one_message_and_a_repeat_scan_sends_none()
    {
        var settings = CreateSettings();
        var module = CreateModule(settings, out var sent);
        var group = Group("PEPE", Quote("Binance", "1000PEPEUSDT", 254.30m, "PEPE"));

        var first = await module.RunAsync([group], anyExchangeFailed: false, CancellationToken.None);
        var second = await module.RunAsync([group], anyExchangeFailed: false, CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Single(sent);
    }

    [Fact]
    public async Task A_higher_milestone_sends_a_new_message()
    {
        var settings = CreateSettings();
        var module = CreateModule(settings, out var sent);

        // Milestone(254.30, 100, 50) = 250.
        await module.RunAsync([Group("PEPE", Quote("Binance", "1000PEPEUSDT", 254.30m, "PEPE"))], false, CancellationToken.None);
        // Milestone(310, 100, 50) = 300, strictly higher: a new message goes out.
        var second = await module.RunAsync([Group("PEPE", Quote("Binance", "1000PEPEUSDT", 310m, "PEPE"))], false, CancellationToken.None);

        Assert.Equal(1, second);
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task A_group_is_recognized_through_any_of_its_aliases_not_just_the_current_key()
    {
        // Seed the history directly under an alias that isn't the group's current primary
        // key — the equivalent of the coin having been notified while a different exchange
        // was its highest priority one.
        var settings = CreateSettings();
        new AlertHistoryStore(cryptoFile).Save(new Dictionary<string, AlertRecord>
        {
            ["PEPEOLD"] = new AlertRecord(DateTimeOffset.UtcNow, 250m)
        });

        var module = CreateModule(settings, out var sent);
        var group = Group("PEPE", Quote("Binance", "1000PEPEUSDT", 254.30m, "PEPE", "PEPEOLD"));

        var messages = await module.RunAsync([group], anyExchangeFailed: false, CancellationToken.None);

        Assert.Equal(0, messages); // same milestone (250): recognized via the "PEPEOLD" alias, not re-sent.
        Assert.Empty(sent);

        var stillOnFile = new AlertHistoryStore(cryptoFile).Load();
        Assert.True(stillOnFile.ContainsKey("PEPEOLD")); // the matched key is kept, not renamed to "PEPE".
        Assert.False(stillOnFile.ContainsKey("PEPE"));
    }

    [Fact]
    public async Task Pruning_is_skipped_entirely_when_an_exchange_failed_this_scan()
    {
        var settings = CreateSettings();
        // An entry whose cooldown has long expired and whose coin isn't in this scan's groups
        // at all (as if the exchange that would report it failed to respond).
        new AlertHistoryStore(cryptoFile).Save(new Dictionary<string, AlertRecord>
        {
            ["GHOST"] = new AlertRecord(DateTimeOffset.UtcNow.AddHours(-100), 100m)
        });

        var module = CreateModule(settings, out _);

        await module.RunAsync([], anyExchangeFailed: true, CancellationToken.None);
        var afterFailedScan = new AlertHistoryStore(cryptoFile).Load();
        Assert.True(afterFailedScan.ContainsKey("GHOST"));

        await module.RunAsync([], anyExchangeFailed: false, CancellationToken.None);
        var afterCleanScan = new AlertHistoryStore(cryptoFile).Load();
        Assert.False(afterCleanScan.ContainsKey("GHOST"));
    }

    [Fact]
    public async Task Coins_are_listed_highest_gain_first()
    {
        var module = CreateModule(CreateSettings(), out var sent);

        await module.RunAsync(
            [Group("SLOWER", Quote("Binance", "SLOWERUSDT", 120m, "SLOWER")), Group("FASTER", Quote("BingX", "FASTER-USDT", 300m, "FASTER"))],
            anyExchangeFailed: false, CancellationToken.None);

        var body = Assert.Single(sent);
        Assert.True(body.IndexOf("FASTER", StringComparison.Ordinal) < body.IndexOf("SLOWER", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_message_shows_the_symbol_the_way_the_exchange_app_lists_it()
    {
        var module = CreateModule(CreateSettings(), out var sent);
        var quote = Quote("BingX", "MONAD-USDT", 150m, "MONAD", "MON") with { DisplaySymbol = "MON-USDT" };

        await module.RunAsync([Group("MONAD", quote)], anyExchangeFailed: false, CancellationToken.None);

        var body = Assert.Single(sent);
        Assert.Contains("BingX MON-USDT", body);
        Assert.DoesNotContain("MONAD-USDT", body);
    }

    [Fact]
    public async Task A_legacy_binance_symbol_key_is_migrated_and_still_recognized()
    {
        // The single-exchange era stored the key as the raw Binance symbol.
        new AlertHistoryStore(cryptoFile).Save(new Dictionary<string, AlertRecord>
        {
            ["HEMIUSDT"] = new AlertRecord(DateTimeOffset.UtcNow, 250m)
        });

        var settings = CreateSettings();
        var module = CreateModule(settings, out var sent);
        var group = Group("HEMI", Quote("Binance", "HEMIUSDT", 254.30m, "HEMI"));

        var messages = await module.RunAsync([group], anyExchangeFailed: false, CancellationToken.None);

        Assert.Equal(0, messages); // same milestone once migrated: not re-sent.
        Assert.Empty(sent);
    }
}
