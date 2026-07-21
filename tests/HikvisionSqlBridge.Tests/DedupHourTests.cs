using HikvisionSqlBridge.Core;
using HikvisionSqlBridge.Core.Model;
using Xunit;

namespace HikvisionSqlBridge.Tests;

public class DedupHourTests
{
    private static AccessEvent Ev(string emp, DateTime time, string serial, VerifyMethod m = VerifyMethod.Face)
        => new() { EmployeeNo = emp, EventTime = time, Method = m, SerialNo = serial, Granted = true };

    [Fact]
    public void Collapses_same_picagem_shifted_by_one_hour_keeping_the_real_one()
    {
        // Safire registou a mesma picagem duas vezes: 10:09:38 (certa) e 11:09:38 (+1h).
        var reference = new DateTime(2026, 7, 21, 10, 9, 35);
        var events = new List<AccessEvent>
        {
            Ev("3", new DateTime(2026, 7, 21, 10, 9, 38), "100"),
            Ev("3", new DateTime(2026, 7, 21, 11, 9, 38), "101"),
        };

        var result = DeviceListener.CollapseHourDuplicates(events, reference);

        var kept = Assert.Single(result);
        Assert.Equal(new DateTime(2026, 7, 21, 10, 9, 38), kept.EventTime); // ficou a correcta
    }

    [Fact]
    public void Keeps_distinct_users_even_with_same_minute_second()
    {
        var reference = new DateTime(2026, 7, 21, 10, 9, 35);
        var events = new List<AccessEvent>
        {
            Ev("3", new DateTime(2026, 7, 21, 10, 9, 38), "100"),
            Ev("4", new DateTime(2026, 7, 21, 10, 9, 38), "101"),
        };

        var result = DeviceListener.CollapseHourDuplicates(events, reference);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Keeps_two_real_picagens_of_same_user_at_different_seconds()
    {
        var reference = new DateTime(2026, 7, 21, 10, 9, 40);
        var events = new List<AccessEvent>
        {
            Ev("3", new DateTime(2026, 7, 21, 10, 9, 38), "100"),
            Ev("3", new DateTime(2026, 7, 21, 10, 9, 55), "101"),
        };

        var result = DeviceListener.CollapseHourDuplicates(events, reference);

        Assert.Equal(2, result.Count);
    }
}
