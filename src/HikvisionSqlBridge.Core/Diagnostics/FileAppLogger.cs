using HikvisionSqlBridge.Core.Configuration;

namespace HikvisionSqlBridge.Core.Diagnostics;

/// <summary>
/// Log em ficheiros rotativos (um por dia) para auditoria. Thread-safe.
/// Apaga automaticamente os ficheiros mais antigos que a retenção configurada.
/// </summary>
public sealed class FileAppLogger : IAppLogger
{
    private readonly LoggingConfig _cfg;
    private readonly string _dir;
    private readonly object _gate = new();
    private DateOnly _lastCleanup = default;

    public FileAppLogger(LoggingConfig cfg)
    {
        _cfg = cfg;
        _dir = Path.IsPathRooted(cfg.Directory)
            ? cfg.Directory
            : Path.Combine(AppContext.BaseDirectory, cfg.Directory);
        Directory.CreateDirectory(_dir);
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERRO", message);

    public void Raw(string terminal, string payload)
    {
        if (!_cfg.LogRawEvents) return;
        var file = Path.Combine(_dir, $"raw-{DateTime.Now:yyyy-MM-dd}.log");
        AppendSafe(file, $"----- {DateTime.Now:HH:mm:ss} {terminal} -----{Environment.NewLine}{payload}{Environment.NewLine}");
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        Console.WriteLine(line);
        var file = Path.Combine(_dir, $"log-{DateTime.Now:yyyy-MM-dd}.log");
        AppendSafe(file, line + Environment.NewLine);
        CleanupIfNeeded();
    }

    private void AppendSafe(string file, string text)
    {
        lock (_gate)
        {
            try { File.AppendAllText(file, text); }
            catch { /* nunca deixar o log rebentar o serviço */ }
        }
    }

    private void CleanupIfNeeded()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today == _lastCleanup) return;
        _lastCleanup = today;

        try
        {
            var cutoff = DateTime.Now.AddDays(-Math.Max(1, _cfg.RetentionDays));
            foreach (var f in Directory.GetFiles(_dir, "*.log"))
                if (File.GetLastWriteTime(f) < cutoff)
                    File.Delete(f);
        }
        catch { /* limpeza é best-effort */ }
    }
}
