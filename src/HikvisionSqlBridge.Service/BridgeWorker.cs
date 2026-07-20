using HikvisionSqlBridge.Core;
using HikvisionSqlBridge.Core.Configuration;
using HikvisionSqlBridge.Core.Data;
using HikvisionSqlBridge.Core.Diagnostics;

namespace HikvisionSqlBridge.Service;

/// <summary>
/// Serviço principal. Ao arrancar, testa a ligação ao SQL Server e lança um
/// listener por cada terminal Hikvision configurado. Cada listener trata da
/// sua própria reconexão automática.
/// </summary>
public sealed class BridgeWorker : BackgroundService
{
    private readonly AppConfig _config;
    private readonly IAppLogger _log;

    public BridgeWorker(AppConfig config, IAppLogger log)
    {
        _config = config;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.Info("=== Hikvision -> SQL Bridge a arrancar ===");

        var repo = new MovimentoRepository(_config.SqlServer, _log);

        if (await repo.TestConnectionAsync(stoppingToken))
            _log.Info($"Ligação ao SQL Server OK ({_config.SqlServer.Server} / {_config.SqlServer.Database}).");
        else
            _log.Warn("Não foi possível ligar ao SQL Server no arranque. O serviço tenta na mesma; verifique a configuração.");

        if (_config.Equipamentos.Count == 0)
        {
            _log.Warn("Nenhum terminal configurado. Adicione equipamentos na configuração.");
            return;
        }

        var tasks = _config.Equipamentos
            .Select(d => new DeviceListener(d, repo, _log).RunAsync(stoppingToken))
            .ToList();

        _log.Info($"{tasks.Count} terminal(is) em escuta.");

        // Fase 2: sincronização automática dos utilizadores (se ligada).
        if (_config.UserSync.Enabled)
        {
            var syncRepo = new UserSyncRepository(_config.SqlServer, _config.UserSync, _log);
            var sync = new UserSyncService(_config, syncRepo, _log);
            tasks.Add(sync.RunAsync(stoppingToken));
        }

        await Task.WhenAll(tasks);

        _log.Info("=== Serviço terminado ===");
    }
}
