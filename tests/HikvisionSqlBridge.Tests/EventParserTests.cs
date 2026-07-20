using System.Text;
using HikvisionSqlBridge.Core.Hikvision;
using HikvisionSqlBridge.Core.Model;
using Xunit;

namespace HikvisionSqlBridge.Tests;

public class EventParserTests
{
    private const string AccessEventXml = """
        <EventNotificationAlert xmlns="http://www.hikvision.com/ver20/XMLSchema" version="2.0">
          <ipAddress>10.4.1.15</ipAddress>
          <portNo>80</portNo>
          <dateTime>2025-06-21T20:00:48+01:00</dateTime>
          <eventType>AccessControllerEvent</eventType>
          <eventState>active</eventState>
          <AccessControllerEvent>
            <majorEventType>5</majorEventType>
            <subEventType>1</subEventType>
            <cardReaderNo>1</cardReaderNo>
            <doorNo>1</doorNo>
            <employeeNoString>489</employeeNoString>
            <serialNo>123</serialNo>
            <currentVerifyMode>face</currentVerifyMode>
            <name>Joao</name>
          </AccessControllerEvent>
        </EventNotificationAlert>
        """;

    [Fact]
    public void TryParse_reads_access_event_fields()
    {
        var ok = EventParser.TryParse(AccessEventXml, "10.0.0.1", out var evt);

        Assert.True(ok);
        Assert.NotNull(evt);
        Assert.Equal("489", evt!.EmployeeNo);
        Assert.Equal(VerifyMethod.Face, evt.Method);
        Assert.Equal("10.4.1.15", evt.TerminalIp);
        Assert.Equal(new DateTime(2025, 6, 21, 20, 0, 48), evt.EventTime);
        Assert.True(evt.Granted);
    }

    [Fact]
    public void TryParse_ignores_events_without_employee()
    {
        var xml = AccessEventXml.Replace("<employeeNoString>489</employeeNoString>", "");
        var ok = EventParser.TryParse(xml, "10.0.0.1", out var evt);

        Assert.False(ok);
        Assert.Null(evt);
    }

    [Fact]
    public void TryParse_ignores_non_access_event_types()
    {
        var xml = AccessEventXml.Replace("AccessControllerEvent", "VideoLoss");
        var ok = EventParser.TryParse(xml, "10.0.0.1", out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryParse_falls_back_to_device_ip_when_missing()
    {
        var xml = AccessEventXml.Replace("<ipAddress>10.4.1.15</ipAddress>", "");
        var ok = EventParser.TryParse(xml, "10.99.99.99", out var evt);

        Assert.True(ok);
        Assert.Equal("10.99.99.99", evt!.TerminalIp);
    }

    [Fact]
    public void ExtractEvents_splits_multiple_blocks_and_keeps_partial_tail()
    {
        var buffer = new StringBuilder();
        buffer.Append("--boundary\r\nContent-Type: application/xml\r\n\r\n");
        buffer.Append(AccessEventXml);
        buffer.Append("\r\n--boundary\r\n\r\n");
        buffer.Append(AccessEventXml);
        buffer.Append("\r\n--boundary\r\n\r\n<EventNotificationAlert><incompl"); // bloco cortado

        var events = HikvisionAlertStreamClient.ExtractEvents(buffer);

        Assert.Equal(2, events.Count());
        // O fragmento incompleto deve permanecer no buffer para o próximo chunk.
        Assert.Contains("<incompl", buffer.ToString());
        Assert.Contains("<EventNotificationAlert>", buffer.ToString());
    }
}
