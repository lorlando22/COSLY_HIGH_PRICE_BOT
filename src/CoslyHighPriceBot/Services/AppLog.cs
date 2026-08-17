using System.Globalization;

namespace CoslyHighPriceBot.Services;

/// <summary>
/// Writes to the console and to a daily file in the Logs folder next to the executable.
/// A static helper is enough: the program is single-shot and single-threaded.
/// </summary>
internal static class AppLog
{
    private const string FilePrefix = "pumps-";
    private const string DateFormat = "yyyy-MM-dd";

    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Logs");

    /// <summary>Turns itself off only if writing fails, so the same error isn't repeated on every line.</summary>
    private static bool fileLoggingEnabled = true;

    public static void Info(string message) => Write("INFO", message, Console.Out);

    public static void Warn(string message) => Write("WARN", message, Console.Out);

    public static void Error(string message) => Write("ERROR", message, Console.Error);

    /// <summary>
    /// Deletes logs older than <paramref name="retentionDays"/> days. Age comes from the
    /// date in the file name, not its modification date: copying the folder shouldn't
    /// make old logs look fresh. 0 means nothing gets deleted.
    /// </summary>
    public static void DeleteOldFiles(int retentionDays)
    {
        if (retentionDays <= 0 || !Directory.Exists(Folder))
            return;

        var cutoff = DateTime.Today.AddDays(-retentionDays);
        var deleted = 0;

        foreach (var file in Directory.EnumerateFiles(Folder, $"{FilePrefix}*.log"))
        {
            var datePart = Path.GetFileNameWithoutExtension(file)[FilePrefix.Length..];

            if (!DateTime.TryParseExact(datePart, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;

            if (date >= cutoff)
                continue;

            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Warn($"Could not delete log {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        if (deleted > 0)
            Info($"{deleted} log(s) older than {retentionDays} days deleted.");
    }

    private static void Write(string level, string message, TextWriter console)
    {
        var now = DateTime.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";

        console.WriteLine(line);

        if (!fileLoggingEnabled)
            return;

        try
        {
            Directory.CreateDirectory(Folder);
            File.AppendAllText(Path.Combine(Folder, $"{FilePrefix}{now.ToString(DateFormat)}.log"), line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not being able to write the log can't be allowed to stop the bot from alerting about a pump.
            fileLoggingEnabled = false;
            console.WriteLine($"{now:yyyy-MM-dd HH:mm:ss} [WARN] Could not write to {Folder} ({ex.Message}). Continuing with console only.");
        }
    }
}
