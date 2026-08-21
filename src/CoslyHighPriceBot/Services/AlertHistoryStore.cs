using System.Text.Json;

namespace CoslyHighPriceBot.Services;

/// <summary>
/// Remembers when each symbol was last alerted about, so the same symbol isn't announced
/// twice in a short window. On disk it's a JSON object mapping symbol to the moment the
/// Telegram message went out:
/// <code>{ "HEMIUSDT": "2026-08-21T13:22:04+00:00" }</code>
/// </summary>
internal sealed class AlertHistoryStore(string filePath)
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public string FileName => Path.GetFileName(filePath);

    public IReadOnlyDictionary<string, DateTimeOffset> Load()
    {
        if (!File.Exists(filePath))
            return Empty();

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(filePath));

            // Older versions stored a plain array of symbols with no timestamp. Those entries
            // are treated as "notified just now": biasing towards suppressing an alert is
            // safer than duplicating one, and the file is rewritten in the new shape on save.
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                var now = DateTimeOffset.UtcNow;
                var migrated = document.RootElement
                    .EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToDictionary(s => s!, _ => now, StringComparer.Ordinal);

                if (migrated.Count > 0)
                    AppLog.Info($"{FileName} is in the old format: {migrated.Count} symbol(s) migrated with the current timestamp.");

                return migrated;
            }

            return document.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetDateTimeOffset(), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            // A corrupted file can't leave the bot unusable: it starts from scratch.
            // The cost is a possible duplicate alert, far smaller than never alerting again.
            AppLog.Warn($"Could not read {FileName} ({ex.Message}). Ignored and rewritten.");
            return Empty();
        }
    }

    public void Save(IReadOnlyDictionary<string, DateTimeOffset> history)
    {
        // The path may point to a folder that doesn't exist yet (e.g. state/ in CI).
        var folder = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        // Sorted so the file has a stable order and its diffs stay readable in git.
        var ordered = history
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        File.WriteAllText(filePath, JsonSerializer.Serialize(ordered, WriteOptions));
    }

    private static Dictionary<string, DateTimeOffset> Empty() => new(StringComparer.Ordinal);
}
