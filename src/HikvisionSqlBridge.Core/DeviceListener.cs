using HikvisionSqlBridge.Core.Configuration;
using HikvisionSqlBridge.Core.Data;
using HikvisionSqlBridge.Core.Diagnostics;
using HikvisionSqlBridge.Core.Hikvision;
using HikvisionSqlBridge.Core.Mapping;
using HikvisionSqlBridge.Core.Model;

namespace HikvisionSqlBridge.Core;

/// <summary>
/// Supervisiona a ligação a UM terminal: fica a ouvir as picagens e, se a
/// ligação cair, volta a ligar automaticamente com backoff progressivo. Cada
/// picagem válida é gravada na base de dados.
/// </summary>
public sealed class DeviceListener
{
    private readonly DeviceConfig _device;
    private readonly MovimentoRepository _repo;
    private readonly IAppLogger _log;

    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    public DeviceListener(DeviceConfig device, MovimentoRepository repo, IAppLogger log)
    {
        _device = device;
        _repo = repo;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = MinBackoff;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = new HikvisionAlertStreamClient(_device, _log);
                await client.ListenAsync(HandleEventAsync, ct);

                // Se saiu sem excepção, esteve ligado e o stream fechou —
                // reconecta de imediato e repõe o backoff.
                _log.Warn($"Ligação ao terminal {_device.DisplayName} terminou. A reconectar...");
                backoff = MinBackoff;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break; // paragem normal do serviço
            }
            catch (Exception ex)
            {
                _log.Error($"Terminal {_device.DisplayName}: {ex.Message}. Nova tentativa em {backoff.TotalSeconds:0}s.");
            }

            if (ct.IsCancellationRequested) break;

            try { await Task.Delay(backoff, ct); }
            catch (OperationCanceledException) { break; }

            // Backoff exponencial até ao máximo; reposto quando reconecta com sucesso.
            backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, backoff.TotalSeconds * 2));
        }

        _log.Info($"Listener do terminal {_device.DisplayName} parado.");
    }

    private async Task HandleEventAsync(AccessEvent evt)
    {
        // O cliente pediu para gravar apenas as picagens válidas (acesso concedido).
        if (!evt.Granted)
        {
            _log.Info($"Acesso negado ignorado: {evt}");
            return;
        }

        var movimento = MovimentoMapper.ToMovimento(evt);
        var inserted = await _repo.InsertAsync(movimento);
        if (inserted)
            _log.Info($"Picagem gravada: utilizador {movimento.IdIdentificador} " +
                      $"({evt.Method}) @ {movimento.IdDataHora:yyyy-MM-dd HH:mm:ss} " +
                      $"terminal {movimento.IdIpTerminal}");
    }
}
