namespace HikvisionSqlBridge.Core;

/// <summary>
/// Calcula a janela (início/fim) de cada consulta AcsEvent só com o relógio do
/// TERMINAL. Entre leituras da hora do terminal usa um cronómetro monotónico
/// (Stopwatch), por isso mudar a data/hora do PC não afecta nada — visto num
/// cliente que muda a data do PC para a frente e depois volta atrás.
/// Garante sempre início &lt; fim (o terminal responde 400 "badJsonContent" se não).
/// </summary>
public sealed class PollWindow
{
    /// <summary>Sobreposição com a consulta anterior (o serialNo evita duplicados).</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Lead = TimeSpan.FromSeconds(5);
    /// <summary>Se o relógio andar para trás, volta a pedir só os últimos minutos.</summary>
    public static readonly TimeSpan BackStep = TimeSpan.FromMinutes(2);
    /// <summary>Janela máxima (ex.: depois de muito tempo sem ligação).</summary>
    public static readonly TimeSpan MaxSpan = TimeSpan.FromDays(3);

    private DateTimeOffset? _anchor;        // hora do terminal lida...
    private TimeSpan _anchorElapsed;        // ...neste instante do cronómetro
    private DateTimeOffset? _lastNow;       // "agora" (hora do terminal) da última consulta com sucesso

    public bool HasDeviceTime => _anchor is not null;

    /// <summary>Regista a hora lida do terminal no instante <paramref name="elapsed"/> do cronómetro.</summary>
    public void SetDeviceTime(DateTimeOffset deviceTime, TimeSpan elapsed)
    {
        _anchor = deviceTime;
        _anchorElapsed = elapsed;
    }

    /// <summary>"Agora" na hora do terminal; sem leitura do terminal usa a hora do PC.</summary>
    public DateTimeOffset Now(TimeSpan elapsed, DateTimeOffset pcNow)
        => _anchor is { } a ? a + (elapsed - _anchorElapsed) : pcNow;

    /// <summary>Janela da próxima consulta para o "agora" <paramref name="now"/> (hora do terminal).</summary>
    public (DateTimeOffset Start, DateTimeOffset End) Next(DateTimeOffset now)
    {
        var end = now + Lead;
        var start = (_lastNow ?? now) - Overlap;

        // O relógio andou para trás (ou a última consulta ficou "no futuro"):
        // nunca enviar início depois do fim.
        if (start >= end - TimeSpan.FromSeconds(1))
            start = now - BackStep;

        if (end - start > MaxSpan)
            start = end - MaxSpan;

        return (start, end);
    }

    /// <summary>A consulta feita com "agora" = <paramref name="now"/> correu bem.</summary>
    public void Success(DateTimeOffset now) => _lastNow = now;
}
