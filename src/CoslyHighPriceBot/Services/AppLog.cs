namespace CoslyHighPriceBot.Services;

/// <summary>
/// Escribe por consola y a un archivo diario en la carpeta Logs, junto al ejecutable.
/// Un helper estático alcanza: el programa es de un solo disparo y de un solo hilo.
/// </summary>
internal static class AppLog
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Logs");

    /// <summary>Se apaga solo si falla la escritura, para no repetir el mismo error en cada línea.</summary>
    private static bool fileLoggingEnabled = true;

    public static void Info(string message) => Write("INFO", message, Console.Out);

    public static void Warn(string message) => Write("WARN", message, Console.Out);

    public static void Error(string message) => Write("ERROR", message, Console.Error);

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
            File.AppendAllText(Path.Combine(Folder, $"pumps-{now:yyyy-MM-dd}.log"), line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Que no se pueda escribir el log no puede impedir que el bot avise del pump.
            fileLoggingEnabled = false;
            console.WriteLine($"{now:yyyy-MM-dd HH:mm:ss} [WARN] No se pudo escribir en {Folder} ({ex.Message}). Sigue sólo por consola.");
        }
    }
}
