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

    public Task RunAsync(CancellationToken ct)
        => _device.IsPollMode ? RunPollAsync(ct) : RunStreamAsync(ct);

    /// <summary>
    /// Modo por consulta (AcsEvent): pergunta ao terminal, de X em X segundos,
    /// que picagens houve, e grava as novas. Funciona em qualquer produto
    /// Hikvision. Cada picagem só é gravada uma vez (dedupe por serialNo).
    /// </summary>
    private async Task RunPollAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _device.PollIntervalSeconds));
        _log.Info($"Terminal {_device.DisplayName}: modo consulta (AcsEvent) a cada {interval.TotalSeconds:0}s. A aguardar picagens...");

        // Só nos interessam picagens a partir do arranque do serviço.
        var since = DateTimeOffset.Now;
        long lastSerial = -1;
        var backoff = MinBackoff;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = new HikvisionAcsEventClient(_device, _log);
                var now = DateTimeOffset.Now;

                // Sobreposição de alguns segundos para não perder picagens na fronteira.
                var start = since.AddSeconds(-10);
                var events = await client.QueryAsync(start, now.AddSeconds(5), ct);

                // Alguns terminais (visto num Safire) registam a MESMA picagem duas
                // vezes com a hora deslocada exactamente 1h (bug de fuso/DST no
                // equipamento). Colapsamos esses duplicados dentro do lote.
                var deduped = CollapseHourDuplicates(events, now.DateTime);

                foreach (var ev in deduped.OrderBy(e => ParseSerial(e.SerialNo)))
                {
                    var serial = ParseSerial(ev.SerialNo);
                    if (serial <= lastSerial) continue; // já tratada
                    await HandleEventAsync(ev);
                }

                // Avança o marcador para além de TODOS os serialNo vistos neste lote
                // (incluindo os duplicados descartados), para que a janela de
                // sobreposição não os volte a trazer no próximo ciclo.
                foreach (var ev in events)
                    lastSerial = Math.Max(lastSerial, ParseSerial(ev.SerialNo));

                since = now;
                backoff = MinBackoff; // consulta correu bem
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error($"Terminal {_device.DisplayName}: {ex.Message}. Nova tentativa em {backoff.TotalSeconds:0}s.");
                try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, backoff.TotalSeconds * 2));
                continue;
            }

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }

        _log.Info($"Listener do terminal {_device.DisplayName} parado.");
    }

    // serialNo é numérico e crescente no Hikvision; usamo-lo para não repetir picagens.
    private static long ParseSerial(string? serial)
        => long.TryParse(serial, out var n) ? n : -1;

    /// <summary>
    /// Colapsa duplicados da mesma picagem que só diferem na hora (deslocamento de
    /// fuso/DST no terminal). Dentro do mesmo lote, agrupa por
    /// (funcionário, método, minuto, segundo) e fica apenas com o evento cuja hora
    /// está mais próxima da hora real — descartando o "fantasma" +/- Nh. Picagens
    /// reais separadas por horas chegam em lotes diferentes, logo nunca são
    /// afectadas.
    /// </summary>
    internal static List<AccessEvent> CollapseHourDuplicates(List<AccessEvent> events, DateTime reference)
        => events
            .GroupBy(e => (e.EmployeeNo, e.Method, e.EventTime.Minute, e.EventTime.Second))
            .Select(g => g.OrderBy(e => Math.Abs((e.EventTime - reference).Ticks)).First())
            .ToList();

    /// <summary>Modo por streaming (ISAPI alertStream) — mais imediato mas nem todos os modelos o suportam.</summary>
    private async Task RunStreamAsync(CancellationToken ct)
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
