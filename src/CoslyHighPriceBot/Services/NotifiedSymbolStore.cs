using System.Text.Json;

namespace CoslyHighPriceBot.Services;

/// <summary>
/// Recuerda de qué símbolos ya se avisó, para no repetir el mensaje mientras siguen
/// por encima del umbral. Es un array JSON de símbolos, por ejemplo: ["HEMIUSDT","COWUSDT"].
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
            // Un archivo corrupto no puede dejar el bot inutilizable: se arranca de cero.
            // El costo es un posible aviso repetido, mucho menor que no avisar nunca más.
            ConsoleLog.Warn($"No se pudo leer {Path.GetFileName(filePath)} ({ex.Message}). Se ignora y se reescribe.");
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    public void Save(IEnumerable<string> symbols)
    {
        // La ruta puede apuntar a una carpeta que todavía no existe (por ejemplo state/ en CI).
        var folder = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        File.WriteAllText(filePath, JsonSerializer.Serialize(symbols.Order(StringComparer.Ordinal), WriteOptions));
    }
}
