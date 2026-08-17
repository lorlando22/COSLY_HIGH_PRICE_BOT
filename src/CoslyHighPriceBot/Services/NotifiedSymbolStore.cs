using System.Text.Json;

namespace CoslyHighPriceBot.Services;

/// <summary>
/// Remembers which symbols have already been notified, so the message isn't repeated
/// while they stay above the threshold. It's a JSON array of symbols, e.g.: ["HEMIUSDT","COWUSDT"].
/// </summary>
internal sealed class NotifiedSymbolStore(string filePath)
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public string FilePath => filePath;

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
            AppLog.Warn($"Could not read {Path.GetFileName(filePath)} ({ex.Message}). Ignored and rewritten.");
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
