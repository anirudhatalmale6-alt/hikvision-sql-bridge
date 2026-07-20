using HikvisionSqlBridge.Core.Hikvision;
using HikvisionSqlBridge.Core.Model;
using Xunit;

namespace HikvisionSqlBridge.Tests;

public class AcsEventClientTests
{
    private const string SampleJson = @"{
      ""AcsEvent"": {
        ""searchID"": ""SIBHIK"",
        ""responseStatusStrg"": ""OK"",
        ""numOfMatches"": 3,
        ""totalMatches"": 3,
        ""InfoList"": [
          { ""major"": 5, ""minor"": 75, ""time"": ""2026-07-20T15:42:10+01:00"", ""name"": ""Francisco"", ""employeeNoString"": ""2"", ""currentVerifyMode"": ""face"", ""serialNo"": 1005 },
          { ""major"": 5, ""minor"": 1,  ""time"": ""2026-07-20T15:41:00+01:00"", ""name"": ""Francisco"", ""employeeNoString"": ""2"", ""currentVerifyMode"": ""fingerprint"", ""serialNo"": 1003 },
          { ""major"": 5, ""minor"": 22, ""time"": ""2026-07-20T15:43:00+01:00"", ""serialNo"": 1006 }
        ]
      }
    }";

    [Fact]
    public void ParseResponse_reads_person_events_and_ignores_door_events()
    {
        var (events, status, count) = HikvisionAcsEventClient.ParseResponse(SampleJson, "192.168.1.117");

        Assert.Equal("OK", status);
        Assert.Equal(3, count);                 // 3 registos no InfoList...
        Assert.Equal(2, events.Count);          // ...mas o evento de porta (sem funcionário) é ignorado

        var face = events[0];
        Assert.Equal("2", face.EmployeeNo);
        Assert.Equal(VerifyMethod.Face, face.Method);
        Assert.Equal("192.168.1.117", face.TerminalIp);
        Assert.True(face.Granted);
        Assert.Equal("1005", face.SerialNo);

        var fp = events[1];
        Assert.Equal("2", fp.EmployeeNo);
        Assert.Equal(VerifyMethod.Fingerprint, fp.Method);
    }

    [Fact]
    public void ParseResponse_detects_more_status_for_pagination()
    {
        var json = @"{ ""AcsEvent"": { ""responseStatusStrg"": ""MORE"", ""InfoList"": [
            { ""time"": ""2026-07-20T15:42:10+01:00"", ""employeeNoString"": ""6"", ""cardNo"": ""123456"", ""serialNo"": 2001 } ] } }";

        var (events, status, count) = HikvisionAcsEventClient.ParseResponse(json, "10.0.0.5");

        Assert.Equal("MORE", status);
        Assert.Equal(1, count);
        Assert.Single(events);
        // Sem currentVerifyMode mas com cartão => método Cartão.
        Assert.Equal(VerifyMethod.Card, events[0].Method);
        Assert.Equal("6", events[0].EmployeeNo);
    }

    [Fact]
    public void ParseResponse_handles_empty_list()
    {
        var json = @"{ ""AcsEvent"": { ""responseStatusStrg"": ""OK"", ""numOfMatches"": 0 } }";
        var (events, status, count) = HikvisionAcsEventClient.ParseResponse(json, "10.0.0.5");

        Assert.Empty(events);
        Assert.Equal(0, count);
        Assert.Equal("OK", status);
    }
}
