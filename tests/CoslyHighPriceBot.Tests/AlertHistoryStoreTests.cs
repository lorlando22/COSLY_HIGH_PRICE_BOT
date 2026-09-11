using CoslyHighPriceBot.Services;

namespace CoslyHighPriceBot.Tests;

public class AlertHistoryStoreTests
{
    private static AlertRecord Record(int hoursAgo, decimal? milestone) =>
        new(DateTimeOffset.UtcNow.AddHours(-hoursAgo), milestone);

    [Fact]
    public void MigrateLegacyKeys_strips_the_quote_asset_suffix()
    {
        var history = new Dictionary<string, AlertRecord> { ["HEMIUSDT"] = Record(1, 250m) };

        var migrated = AlertHistoryStore.MigrateLegacyKeys(history, "USDT", normalizeMultiplier: false);

        Assert.True(migrated.ContainsKey("HEMI"));
        Assert.False(migrated.ContainsKey("HEMIUSDT"));
        Assert.Equal(250m, migrated["HEMI"].Milestone);
    }

    [Fact]
    public void MigrateLegacyKeys_also_normalizes_a_crypto_multiplier()
    {
        var history = new Dictionary<string, AlertRecord> { ["1000PEPEUSDT"] = Record(1, 100m) };

        var migrated = AlertHistoryStore.MigrateLegacyKeys(history, "USDT", normalizeMultiplier: true);

        Assert.True(migrated.ContainsKey("PEPE"));
    }

    [Fact]
    public void MigrateLegacyKeys_does_not_touch_a_key_that_is_not_the_quote_asset_suffixed()
    {
        var history = new Dictionary<string, AlertRecord> { ["HEMI"] = Record(1, 250m) };

        var migrated = AlertHistoryStore.MigrateLegacyKeys(history, "USDT", normalizeMultiplier: false);

        Assert.True(migrated.ContainsKey("HEMI"));
    }

    [Fact]
    public void MigrateLegacyKeys_resolves_a_collision_by_keeping_the_higher_milestone()
    {
        // Two distinct legacy keys that both migrate to the same coin name: the plain symbol,
        // and a differently-multiplied one that normalizes to the same alias.
        var history = new Dictionary<string, AlertRecord>
        {
            ["FOOUSDT"] = Record(2, 100m),
            ["1000FOOUSDT"] = Record(1, 200m),
        };

        var migrated = AlertHistoryStore.MigrateLegacyKeys(history, "USDT", normalizeMultiplier: true);

        Assert.Equal(["FOO"], migrated.Keys);
        Assert.Equal(200m, migrated["FOO"].Milestone);
    }

    [Theory]
    [InlineData(100, 200, 200)]
    [InlineData(200, 100, 200)]
    public void PreferHigherMilestone_keeps_the_higher_one(int a, int b, int expected)
    {
        var result = AlertHistoryStore.PreferHigherMilestone(Record(1, a), Record(1, b));

        Assert.Equal(expected, result.Milestone);
    }

    [Fact]
    public void PreferHigherMilestone_breaks_a_tie_with_the_more_recent_timestamp()
    {
        var older = Record(5, 100m);
        var newer = Record(1, 100m);

        var result = AlertHistoryStore.PreferHigherMilestone(older, newer);

        Assert.Equal(newer.NotifiedAt, result.NotifiedAt);
    }
}
