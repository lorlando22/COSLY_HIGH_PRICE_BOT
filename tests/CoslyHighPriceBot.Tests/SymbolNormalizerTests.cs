using CoslyHighPriceBot.Services;

namespace CoslyHighPriceBot.Tests;

public class SymbolNormalizerTests
{
    [Theory]
    [InlineData("1000PEPE", "PEPE", 1000)]
    [InlineData("1MBABYDOGE", "BABYDOGE", 1_000_000)]
    [InlineData("SHIB1000", "SHIB", 1000)]
    [InlineData("1000000MOG", "MOG", 1_000_000)]
    [InlineData("10000SATS", "SATS", 10_000)]
    public void Normalize_strips_a_multiplier_that_sits_against_a_letter(string baseName, string expectedName, decimal expectedMultiplier)
    {
        var (name, multiplier) = SymbolNormalizer.Normalize(baseName);

        Assert.Equal(expectedName, name);
        Assert.Equal(expectedMultiplier, multiplier);
    }

    [Theory]
    [InlineData("1INCH")]
    [InlineData("B2")]
    [InlineData("LUNA2")]
    [InlineData("API3")]
    [InlineData("BANANAS31")]
    [InlineData("KODEX200")]
    public void Normalize_leaves_a_bare_trailing_or_leading_digit_untouched(string baseName)
    {
        var (name, multiplier) = SymbolNormalizer.Normalize(baseName);

        Assert.Equal(baseName, name);
        Assert.Equal(1m, multiplier);
    }
}
