using HikvisionSqlBridge.Core;
using Xunit;

namespace HikvisionSqlBridge.Tests;

public class PollWindowTests
{
    private static DateTimeOffset T(string s) => DateTimeOffset.Parse(s);
    private static TimeSpan Sec(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void Window_follows_the_terminal_clock_and_ignores_PC_date_changes()
    {
        // 18/11 15:27: terminal certo; o cliente põe o PC em 20/11 e depois volta atrás.
        var w = new PollWindow();
        w.SetDeviceTime(T("2026-11-18T15:27:00+00:00"), Sec(0));

        var now1 = w.Now(Sec(3), pcNow: T("2026-11-20T15:27:03+00:00")); // PC 2 dias à frente
        Assert.Equal(T("2026-11-18T15:27:03+00:00"), now1);
        var (s1, e1) = w.Next(now1);
        Assert.True(s1 < e1);
        w.Success(now1);

        var now2 = w.Now(Sec(6), pcNow: T("2026-11-18T15:27:06+00:00")); // PC voltou à data certa
        var (s2, e2) = w.Next(now2);
        Assert.Equal(T("2026-11-18T15:26:53+00:00"), s2);                 // continua de onde estava
        Assert.Equal(T("2026-11-18T15:27:11+00:00"), e2);
    }

    [Fact]
    public void Start_is_never_after_end_when_the_clock_goes_back()
    {
        // Antes (v0.4.12) foi enviado startTime 20/11 e endTime 18/11 -> HTTP 400 até reiniciar.
        var w = new PollWindow();
        w.Success(T("2026-11-20T15:31:12+00:00"));           // última consulta "no futuro"
        var (s, e) = w.Next(T("2026-11-18T15:31:30+00:00"));  // relógio voltou 2 dias atrás
        Assert.True(s < e);
        Assert.Equal(T("2026-11-18T15:29:30+00:00"), s);       // pede os últimos 2 minutos
    }

    [Fact]
    public void Terminal_clock_set_back_days_keeps_collecting()
    {
        // Caso de 29/10: o terminal passa a marcar 2 dias atrás.
        var w = new PollWindow();
        w.SetDeviceTime(T("2026-10-29T08:45:00+00:00"), Sec(0));
        w.Success(w.Now(Sec(3), T("2026-10-29T08:45:03+00:00")));

        w.SetDeviceTime(T("2026-10-27T08:45:30+00:00"), Sec(30));      // relógio do terminal mudou
        var now = w.Now(Sec(33), T("2026-10-29T08:45:33+00:00"));
        var (s, e) = w.Next(now);
        Assert.True(s < e);
        Assert.True(e > now);                                           // a janela inclui o "agora" do terminal
    }

    [Fact]
    public void Without_terminal_time_falls_back_to_PC_time_safely()
    {
        var w = new PollWindow();
        Assert.False(w.HasDeviceTime);
        var now = w.Now(Sec(0), T("2026-11-20T10:00:00+00:00"));
        Assert.Equal(T("2026-11-20T10:00:00+00:00"), now);
        w.Success(T("2026-11-23T10:00:00+00:00"));                     // PC esteve à frente
        var (s, e) = w.Next(T("2026-11-20T10:00:03+00:00"));            // e voltou
        Assert.True(s < e);
    }

    [Fact]
    public void Very_long_gaps_are_capped()
    {
        var w = new PollWindow();
        w.Success(T("2026-11-01T00:00:00+00:00"));
        var (s, e) = w.Next(T("2026-11-20T00:00:00+00:00"));
        Assert.Equal(PollWindow.MaxSpan, e - s);
    }
}
