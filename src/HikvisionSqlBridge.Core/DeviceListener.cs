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

        // A janela segue o relógio do TERMINAL (lido a cada 30 s e, entre leituras,
        // contado por um cronómetro monotónico). Mudar a data/hora do PC — ou do
        // terminal — já não deixa o serviço preso a pedir janelas impossíveis.
        var window = new PollWindow();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var lastTimeCheck = TimeSpan.MinValue;
        long lastSerial = -1;
        var backoff = MinBackoff;
        var clocksDiffer = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = new HikvisionAcsEventClient(_device, _log);

                if (lastTimeCheck == TimeSpan.MinValue || clock.Elapsed - lastTimeCheck >= TimeCheckInterval)
                {
                    var deviceTime = await client.GetDeviceTimeAsync(ct);
                    lastTimeCheck = clock.Elapsed;
                    if (deviceTime is not null)
                    {
                        window.SetDeviceTime(deviceTime.Value, clock.Elapsed);
                        clocksDiffer = WarnIfClocksDiffer(deviceTime.Value, DateTimeOffset.Now, clocksDiffer);
                    }
                }

                var now = window.Now(clock.Elapsed, DateTimeOffset.Now);
                var (start, end) = window.Next(now);
                var events = await client.QueryAsync(start, end, ct);

                // Alguns terminais (visto num Safire) registam a MESMA picagem duas
                // vezes com a hora deslocada exactamente 1h (bug de fuso/DST no
                // equipamento). Colapsamos esses duplicados dentro do lote.
                // As picagens são gravadas com a hora do próprio terminal.
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

                window.Success(now);
                backoff = MinBackoff; // consulta correu bem
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error($"Terminal {_device.DisplayName}: {ex.Message}. Nova tentativa em {backoff.TotalSeconds:0}s.");
                // Um erro pode ser o relógio do terminal ter mudado: volta a lê-lo já.
                lastTimeCheck = TimeSpan.MinValue;
                try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, backoff.TotalSeconds * 2));
                continue;
            }

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }

        _log.Info($"Listener do terminal {_device.DisplayName} parado.");
    }

    private static readonly TimeSpan TimeCheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Avisa (uma vez por mudança) quando o relógio do terminal e o do PC diferem
    /// 2 horas ou mais. Só informativo: a recolha usa sempre o relógio do terminal
    /// e as picagens ficam com a hora do terminal.
    /// </summary>
    private bool WarnIfClocksDiffer(DateTimeOffset deviceTime, DateTimeOffset pcTime, bool wasDifferent)
    {
        var differ = ClocksDiffer(deviceTime, pcTime);
        if (differ && !wasDifferent)
            _log.Warn($"Terminal {_device.DisplayName}: o relógio do terminal ({deviceTime:yyyy-MM-dd HH:mm:ss}) e o do PC " +
                      $"({pcTime:yyyy-MM-dd HH:mm:ss}) estão diferentes. A recolha continua normalmente e as picagens " +
                      "ficam com a hora do terminal. Confirme qual dos dois está certo.");
        else if (!differ && wasDifferent)
            _log.Info($"Terminal {_device.DisplayName}: o relógio do terminal e o do PC voltaram a estar de acordo.");
        return differ;
    }

    internal static bool ClocksDiffer(DateTimeOffset deviceTime, DateTimeOffset pcTime)
        => (deviceTime - pcTime).Duration() >= TimeSpan.FromHours(2);

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
