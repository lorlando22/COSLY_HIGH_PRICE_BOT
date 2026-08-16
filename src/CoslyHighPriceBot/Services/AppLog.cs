using System.Globalization;

namespace CoslyHighPriceBot.Services;

/// <summary>
/// Escribe por consola y a un archivo diario en la carpeta Logs, junto al ejecutable.
/// Un helper estático alcanza: el programa es de un solo disparo y de un solo hilo.
/// </summary>
internal static class AppLog
{
    private const string FilePrefix = "pumps-";
    private const string DateFormat = "yyyy-MM-dd";

    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Logs");

    /// <summary>Se apaga solo si falla la escritura, para no repetir el mismo error en cada línea.</summary>
    private static bool fileLoggingEnabled = true;

    public static void Info(string message) => Write("INFO", message, Console.Out);

    public static void Warn(string message) => Write("WARN", message, Console.Out);

    public static void Error(string message) => Write("ERROR", message, Console.Error);

    /// <summary>
    /// Borra los logs con más días de antigüedad que <paramref name="retentionDays"/>.
    /// La antigüedad sale de la fecha del nombre del archivo, no de su fecha de
    /// modificación: copiar la carpeta no debería rejuvenecer los logs.
    /// Con 0 no se borra nada.
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
                Warn($"No se pudo borrar el log {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        if (deleted > 0)
            Info($"{deleted} log(s) de más de {retentionDays} días eliminados.");
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
            // Que no se pueda escribir el log no puede impedir que el bot avise del pump.
            fileLoggingEnabled = false;
            console.WriteLine($"{now:yyyy-MM-dd HH:mm:ss} [WARN] No se pudo escribir en {Folder} ({ex.Message}). Sigue sólo por consola.");
        }
    }
}
