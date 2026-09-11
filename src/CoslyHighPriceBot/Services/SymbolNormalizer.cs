namespace CoslyHighPriceBot.Services;

/// <summary>
/// Strips a leading or trailing "large supply" multiplier from a crypto base name, so the
/// same coin traded as 1000PEPE (BingX, Bybit) and PEPE (Binance) compares apples to apples
/// once <see cref="Models.CoinGroup"/> tries to line it up across exchanges: the multiplier
/// changes the quoted price by exactly that factor without changing the 24h percentage.
/// Only ever applied to crypto — tokenized stocks never carry one.
/// </summary>
internal static class SymbolNormalizer
{
    // Longest first: "10000" must be tried before "1000", or "10000SATS" would be cut to
    // "0SATS" instead of "SATS".
    private static readonly (string Token, decimal Multiplier)[] Tokens =
    [
        ("10000000", 10_000_000m),
        ("1000000", 1_000_000m),
        ("100000", 100_000m),
        ("10000", 10_000m),
        ("1000", 1_000m),
        ("1M", 1_000_000m),
    ];

    /// <summary>
    /// Splits a raw base name into its normalized alias and the multiplier it carried (1 if
    /// none). "1000PEPE" -> ("PEPE", 1000), "SHIB1000" -> ("SHIB", 1000). A name is only cut
    /// when a token sits right against a letter (the digits immediately followed by a letter
    /// for a prefix, or immediately preceded by one for a suffix): that's what leaves
    /// "1INCH", "B2", "LUNA2", "API3" and "BANANAS31" untouched, since none of them has a
    /// full multiplier token in that position.
    /// </summary>
    public static (string Name, decimal Multiplier) Normalize(string baseName)
    {
        foreach (var (token, multiplier) in Tokens)
        {
            if (baseName.StartsWith(token, StringComparison.Ordinal) &&
                baseName.Length > token.Length && char.IsAsciiLetterUpper(baseName[token.Length]))
                return (baseName[token.Length..], multiplier);
        }

        foreach (var (token, multiplier) in Tokens)
        {
            if (token == "1M")
                continue; // "1M" is only ever seen as a prefix (1MBABYDOGE); no suffix case exists.

            if (baseName.EndsWith(token, StringComparison.Ordinal) &&
                baseName.Length > token.Length && char.IsAsciiLetterUpper(baseName[^(token.Length + 1)]))
                return (baseName[..^token.Length], multiplier);
        }

        return (baseName, 1m);
    }
}
