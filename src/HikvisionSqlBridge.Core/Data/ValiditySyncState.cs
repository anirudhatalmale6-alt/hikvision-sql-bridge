using System.Text.Json;

namespace HikvisionSqlBridge.Core.Data;

/// <summary>
/// Guarda, por funcionário (employeeNo/ID_NUMERO), a última data de fim de
/// validade que ficou igual dos dois lados (SQL e terminais). Serve para a
/// sincronização de validade saber QUAL lado foi alterado desde a última vez:
/// se só um lado mudou, esse manda; se os dois mudaram, aplica-se a regra de
/// desempate. É um simples ficheiro JSON ao lado do config.json.
/// </summary>
public sealed class ValiditySyncState
{
    private readonly string _path;
    private readonly Dictionary<int, DateTime> _last;

    private ValiditySyncState(string path, Dictionary<int, DateTime> last)
    {
        _path = path;
        _last = last;
    }

    public static ValiditySyncState Load(string path)
    {
        var map = new Dictionary<int, DateTime>();
        try
        {
            if (File.Exists(path))
            {
                var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                          ?? new Dictionary<string, string>();
                foreach (var kv in raw)
                    if (int.TryParse(kv.Key, out var id) && DateTime.TryParse(kv.Value, out var d))
                        map[id] = d.Date;
            }
        }
        catch { /* estado corrompido: recomeça vazio, não é crítico */ }
        return new ValiditySyncState(path, map);
    }

    /// <summary>Última data sincronizada deste funcionário, ou null se nunca sincronizou.</summary>
    public DateTime? Get(int idNumero) => _last.TryGetValue(idNumero, out var d) ? d : null;

    /// <summary>Regista a nova data comum (já igual dos dois lados).</summary>
    public void Set(int idNumero, DateTime date) => _last[idNumero] = date.Date;

    public void Save()
    {
        var raw = _last.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.ToString("yyyy-MM-dd"));
        var json = JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }
}
