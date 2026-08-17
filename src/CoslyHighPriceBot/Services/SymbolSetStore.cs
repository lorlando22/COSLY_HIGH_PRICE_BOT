using System.Text.Json;

namespace CoslyHighPriceBot.Services;

/// <summary>
/// A JSON array of symbols on disk, e.g.: ["HEMIUSDT","COWUSDT"]. Three files share this
/// shape: the crypto symbols already notified, the tokenized-stock ones, and the
/// read-only catalog of which base assets are tokenized stocks.
/// </summary>
internal sealed class SymbolSetStore(string filePath)
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public string FilePath => filePath;

    public string FileName => Path.GetFileName(filePath);

    public IReadOnlySet<string> Load()
    {
        if (!File.Exists(filePath))
            return new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var symbols = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(filePath)) ?? [];
            return symbols.ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            // A corrupted file can't leave the bot unusable: it starts from scratch.
            // The cost is a possible duplicate alert, far smaller than never alerting again.
            AppLog.Warn($"Could not read {FileName} ({ex.Message}). Ignored and rewritten.");
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    public void Save(IEnumerable<string> symbols)
    {
        // The path may point to a folder that doesn't exist yet (e.g. state/ in CI).
        var folder = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        File.WriteAllText(filePath, JsonSerializer.Serialize(symbols.Order(StringComparer.Ordinal), WriteOptions));
    }
}
