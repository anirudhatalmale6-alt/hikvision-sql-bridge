using System.Text.Json;

namespace HikvisionSqlBridge.Core.Configuration;

/// <summary>
/// Lê e grava o config.json. É o mesmo formato usado pelo serviço e pela janela
/// de configuração, por isso a janela e o serviço partilham exactamente a mesma
/// configuração.
/// </summary>
public static class ConfigStore
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Caminho por omissão: config.json ao lado do executável.</summary>
    public static string DefaultPath =>
        Path.Combine(AppContext.BaseDirectory, "config.json");

    public static bool Exists(string path) => File.Exists(path);

    /// <summary>Lê o config.json. Lança se o ficheiro não existir ou for inválido.</summary>
    public static AppConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options)
               ?? throw new InvalidOperationException("config.json inválido.");
        // Os ficheiros de estado (ex.: validade) ficam ao lado do config.json.
        cfg.BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? cfg.BaseDirectory;
        return cfg;
    }

    /// <summary>Lê o config.json se existir; caso contrário devolve uma configuração vazia.</summary>
    public static AppConfig LoadOrNew(string path)
        => File.Exists(path) ? Load(path) : new AppConfig();

    public static void Save(string path, AppConfig config)
        => File.WriteAllText(path, JsonSerializer.Serialize(config, Options));
}
