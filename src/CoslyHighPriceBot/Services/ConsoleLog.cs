namespace CoslyHighPriceBot.Services;

/// <summary>Salida por consola. Un helper estático alcanza: el programa es de un solo disparo.</summary>
internal static class ConsoleLog
{
    public static void Info(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    public static void Warn(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] AVISO: {message}");

    public static void Error(string message) =>
        Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] ERROR: {message}");
}
