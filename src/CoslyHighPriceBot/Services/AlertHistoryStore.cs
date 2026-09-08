using System.Text.Json;

namespace CoslyHighPriceBot.Services;

/// <summary>When a symbol's message went out, and the milestone it announced.</summary>
internal readonly record struct AlertRecord(DateTimeOffset NotifiedAt, decimal? Milestone);

/// <summary>
/// Remembers when each symbol was last alerted about and at which milestone, so the same
/// symbol isn't announced twice for the same climb. On disk it's a JSON object mapping
/// symbol to the moment the Telegram message went out and the milestone it reported:
/// <code>{ "HEMIUSDT": { "notifiedAt": "2026-08-21T13:22:04+00:00", "milestone": 250 } }</code>
/// A missing or null milestone means "entry from an older format, milestone unknown" — see
/// <see cref="Load"/>.
/// </summary>
internal sealed class AlertHistoryStore(string filePath)
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string FileName => Path.GetFileName(filePath);

    public IReadOnlyDictionary<string, AlertRecord> Load()
    {
        if (!File.Exists(filePath))
            return Empty();

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(filePath));

            // Oldest format: a plain array of symbols with no timestamp and no milestone.
            // Those entries are treated as "notified just now, milestone unknown": biasing
            // towards suppressing an alert is safer than duplicating one, and the file is
            // rewritten in the new shape on save.
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                var now = DateTimeOffset.UtcNow;
                var migrated = document.RootElement
                    .EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToDictionary(s => s!, _ => new AlertRecord(now, null), StringComparer.Ordinal);

                if (migrated.Count > 0)
                    AppLog.Info($"{FileName} is in the old format: {migrated.Count} symbol(s) migrated with the current timestamp.");

                return migrated;
            }

            // Two more formats share the "object" shape and are told apart per entry: a
            // symbol -> ISO string (the format before milestones existed, milestone unknown)
            // and a symbol -> { notifiedAt, milestone } object (the current one).
            var result = new Dictionary<string, AlertRecord>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? new AlertRecord(property.Value.GetDateTimeOffset(), null)
                    : new AlertRecord(
                        property.Value.GetProperty("notifiedAt").GetDateTimeOffset(),
                        property.Value.TryGetProperty("milestone", out var milestone) && milestone.ValueKind != JsonValueKind.Null
                            ? milestone.GetDecimal()
                            : null);
            }

            return result;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            // A corrupted file can't leave the bot unusable: it starts from scratch.
            // The cost is a possible duplicate alert, far smaller than never alerting again.
            AppLog.Warn($"Could not read {FileName} ({ex.Message}). Ignored and rewritten.");
            return Empty();
        }
    }

    public void Save(IReadOnlyDictionary<string, AlertRecord> history)
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

    private static Dictionary<string, AlertRecord> Empty() => new(StringComparer.Ordinal);
}
