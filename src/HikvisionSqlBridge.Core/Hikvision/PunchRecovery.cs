using HikvisionSqlBridge.Core.Model;

namespace HikvisionSqlBridge.Core.Hikvision;

/// <summary>Uma picagem lida da memória do terminal, pronta a recuperar para o SQL.</summary>
public sealed class RecoveryItem
{
    public required AccessEvent Event { get; init; }
    public long Serial { get; init; }

    /// <summary>Hora tal como o terminal a guardou.</summary>
    public DateTime TerminalTime { get; init; }

    /// <summary>Hora a gravar (corrigida se o relógio do terminal estava desacertado).</summary>
    public DateTime CorrectedTime { get; set; }

    /// <summary>Nota para o relatório (ex.: relógio corrigido, duplicado de fuso).</summary>
    public string? Note { get; set; }

    /// <summary>True se não deve ser gravada (duplicado de fuso).</summary>
    public bool Skip { get; set; }

    public TimeSpan Shift => CorrectedTime - TerminalTime;
}

/// <summary>Correcção manual: soma <see cref="Shift"/> às picagens com serialNo entre From e To.</summary>
public sealed record ManualShift(long FromSerial, long ToSerial, TimeSpan Shift);

/// <summary>
/// Recuperação de picagens que ficaram só no terminal (ex.: quando o relógio do
/// terminal foi acertado para a data errada e o serviço deixou de as receber).
/// O serialNo do Hikvision é sempre crescente, por isso a ordem real das
/// picagens é a ordem do serialNo — mesmo que a hora do terminal tenha andado
/// para trás. É assim que detectamos o salto do relógio e o corrigimos.
/// </summary>
public static class PunchRecovery
{
    /// <summary>Saltos para trás menores que isto (ex.: mudança de hora de verão) não se corrigem.</summary>
    internal static readonly TimeSpan JumpThreshold = TimeSpan.FromHours(2);

    public static List<RecoveryItem> Plan(IEnumerable<AccessEvent> events, IReadOnlyList<ManualShift> manual, DateTime now)
    {
        var items = events
            .Select(e => (e, serial: long.TryParse(e.SerialNo, out var n) ? n : -1))
            .Where(x => x.serial >= 0)
            .GroupBy(x => x.serial).Select(g => g.First())
            .OrderBy(x => x.serial)
            .Select(x => new RecoveryItem
            {
                Event = x.e,
                Serial = x.serial,
                TerminalTime = x.e.EventTime,
                CorrectedTime = x.e.EventTime,
            })
            .ToList();

        if (manual.Count > 0)
            ApplyManual(items, manual);
        else
            ApplyAuto(items, now);

        MarkHourDuplicates(items);
        return items;
    }

    private static void ApplyManual(List<RecoveryItem> items, IReadOnlyList<ManualShift> manual)
    {
        foreach (var it in items)
        {
            var m = manual.FirstOrDefault(s => it.Serial >= s.FromSerial && it.Serial <= s.ToSerial);
            if (m is null) continue;
            it.CorrectedTime = it.TerminalTime + m.Shift;
            it.Note = $"hora corrigida {FormatShift(m.Shift)} (manual)";
        }
    }

    private static void ApplyAuto(List<RecoveryItem> items, DateTime now)
    {
        DateTime? lastCorrected = null;
        var shift = TimeSpan.Zero;

        foreach (var it in items)
        {
            var raw = it.TerminalTime;

            if (shift != TimeSpan.Zero && (raw >= lastCorrected || raw + shift > now))
                shift = TimeSpan.Zero; // o relógio do terminal voltou a estar certo

            if (shift == TimeSpan.Zero && lastCorrected is not null && raw < lastCorrected.Value - JumpThreshold)
                shift = EstimateShift(lastCorrected.Value - raw);

            if (shift != TimeSpan.Zero)
            {
                it.CorrectedTime = raw + shift;
                it.Note = $"relógio do terminal atrasado: hora corrigida {FormatShift(shift)}";
            }

            lastCorrected = lastCorrected is null || it.CorrectedTime > lastCorrected
                ? it.CorrectedTime
                : lastCorrected;
        }
    }

    /// <summary>
    /// Quanto o relógio andou para trás. Só sabemos o mínimo (a diferença para a
    /// picagem anterior); quem acerta mal a data costuma errar dias inteiros com a
    /// hora certa, por isso arredondamos para cima ao dia (ou à hora, se for menos
    /// de ~1 dia).
    /// </summary>
    internal static TimeSpan EstimateShift(TimeSpan backwards)
        => backwards >= TimeSpan.FromHours(20)
            ? TimeSpan.FromDays(Math.Ceiling(backwards.TotalDays))
            : TimeSpan.FromHours(Math.Ceiling(backwards.TotalHours));

    /// <summary>Mesma pessoa, mesmo método, serialNo vizinho e hora a exactamente 1h/2h: é o fantasma de fuso.</summary>
    private static void MarkHourDuplicates(List<RecoveryItem> items)
    {
        for (int i = 1; i < items.Count; i++)
        {
            for (int j = Math.Max(0, i - 3); j < i; j++)
            {
                var a = items[j];
                var b = items[i];
                if (a.Skip || a.Event.EmployeeNo != b.Event.EmployeeNo || a.Event.Method != b.Event.Method)
                    continue;
                var diff = (b.CorrectedTime - a.CorrectedTime).Duration();
                if ((Math.Abs(diff.TotalSeconds - 3600) <= 2 || Math.Abs(diff.TotalSeconds - 7200) <= 2))
                {
                    b.Skip = true;
                    b.Note = $"duplicado de fuso do serial {a.Serial} (ignorado)";
                    break;
                }
            }
        }
    }

    public static string FormatShift(TimeSpan s)
    {
        var sign = s < TimeSpan.Zero ? "-" : "+";
        var d = s.Duration();
        return d.TotalHours >= 24 && d.Hours == 0 && d.Minutes == 0
            ? $"{sign}{(int)d.TotalDays} dia(s)"
            : $"{sign}{d.TotalHours:0.##}h";
    }
}
