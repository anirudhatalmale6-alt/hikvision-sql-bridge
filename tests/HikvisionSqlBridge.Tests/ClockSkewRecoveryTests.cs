using HikvisionSqlBridge.Core;
using HikvisionSqlBridge.Core.Hikvision;
using HikvisionSqlBridge.Core.Model;
using Xunit;

namespace HikvisionSqlBridge.Tests;

public class ClockSkewRecoveryTests
{
    private static AccessEvent Ev(long serial, string time, string emp = "00137")
        => new()
        {
            SerialNo = serial.ToString(),
            EventTime = DateTime.Parse(time),
            EmployeeNo = emp,
            Method = VerifyMethod.Face,
            TerminalIp = "10.0.0.1",
            Granted = true,
        };

    [Fact]
    public void Terminal_clock_set_two_days_back_is_detected_and_corrected_until_fixed()
    {
        var events = new[]
        {
            Ev(10, "2026-10-29 08:30:26", "00214"),   // última picagem com o relógio certo
            Ev(12, "2026-10-27 09:02:10", "00117"),   // relógio 2 dias atrás (real 29/10 09:02)
            Ev(15, "2026-10-27 13:00:00", "00189"),
            Ev(20, "2026-10-28 08:10:00", "00117"),   // real 30/10 08:10
            Ev(30, "2026-11-02 07:35:33", "00137"),   // relógio já acertado
            Ev(31, "2026-11-02 08:05:22", "00167"),
        };

        var plan = PunchRecovery.Plan(events, Array.Empty<ManualShift>(), DateTime.Parse("2026-11-02 17:00:00"));

        Assert.Equal(DateTime.Parse("2026-10-29 08:30:26"), plan[0].CorrectedTime);
        Assert.Equal(DateTime.Parse("2026-10-29 09:02:10"), plan[1].CorrectedTime);
        Assert.Equal(DateTime.Parse("2026-10-29 13:00:00"), plan[2].CorrectedTime);
        Assert.Equal(DateTime.Parse("2026-10-30 08:10:00"), plan[3].CorrectedTime);
        Assert.Equal(DateTime.Parse("2026-11-02 07:35:33"), plan[4].CorrectedTime);
        Assert.Equal(DateTime.Parse("2026-11-02 08:05:22"), plan[5].CorrectedTime);
        Assert.NotNull(plan[1].Note);
        Assert.Null(plan[4].Note);
    }

    [Fact]
    public void Small_backwards_step_like_summer_time_change_is_not_corrected()
    {
        var events = new[]
        {
            Ev(1, "2026-10-29 08:39:25"),          // terminal ainda em hora de verão
            Ev(2, "2026-10-29 08:06:01", "00212"), // acertado para a hora de inverno
        };

        var plan = PunchRecovery.Plan(events, Array.Empty<ManualShift>(), DateTime.Parse("2026-10-29 12:00:00"));

        Assert.All(plan, it => Assert.Equal(it.TerminalTime, it.CorrectedTime));
    }

    [Fact]
    public void Manual_adjustment_applies_only_to_the_serial_range()
    {
        var events = new[] { Ev(5, "2026-10-27 09:00:00"), Ev(9, "2026-11-02 07:00:00") };
        var manual = new[] { new ManualShift(1, 8, TimeSpan.FromHours(48)) };

        var plan = PunchRecovery.Plan(events, manual, DateTime.Parse("2026-11-02 17:00:00"));

        Assert.Equal(DateTime.Parse("2026-10-29 09:00:00"), plan[0].CorrectedTime);
        Assert.Equal(DateTime.Parse("2026-11-02 07:00:00"), plan[1].CorrectedTime);
    }

    [Fact]
    public void Plan_orders_by_serial_and_drops_repeated_serials()
    {
        var events = new[] { Ev(3, "2026-10-29 10:00:00"), Ev(1, "2026-10-29 09:00:00"), Ev(3, "2026-10-29 10:00:00") };

        var plan = PunchRecovery.Plan(events, Array.Empty<ManualShift>(), DateTime.Parse("2026-10-29 12:00:00"));

        Assert.Equal(new long[] { 1, 3 }, plan.Select(p => p.Serial));
    }

    [Theory]
    [InlineData(-172805, true)]      // 2 dias de diferença -> avisa
    [InlineData(3600, false)]        // 1h (fuso/hora de verão) -> não avisa
    [InlineData(40, false)]          // segundos de deriva -> não avisa
    public void ClocksDiffer_only_from_two_hours(int deviceMinusPcSeconds, bool expected)
    {
        var pc = DateTimeOffset.Parse("2026-11-20T15:00:00+00:00");
        Assert.Equal(expected, DeviceListener.ClocksDiffer(pc.AddSeconds(deviceMinusPcSeconds), pc));
    }

    [Fact]
    public void ParseDeviceTime_reads_isapi_xml()
    {
        const string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Time version=\"2.0\"><timeMode>NTP</timeMode>" +
                           "<localTime>2026-10-27T08:45:00+00:00</localTime><timeZone>CST+0:00:00</timeZone></Time>";
        Assert.Equal(DateTimeOffset.Parse("2026-10-27T08:45:00+00:00"), HikvisionAcsEventClient.ParseDeviceTime(xml));
    }

    [Fact]
    public void ParseDeviceTime_reads_json()
        => Assert.Equal(DateTimeOffset.Parse("2026-11-02T07:00:00+01:00"),
            HikvisionAcsEventClient.ParseDeviceTime("{\"Time\":{\"localTime\":\"2026-11-02T07:00:00+01:00\"}}"));
}
