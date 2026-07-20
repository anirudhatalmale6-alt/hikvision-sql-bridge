using HikvisionSqlBridge.Core.Hikvision;
using Xunit;

namespace HikvisionSqlBridge.Tests;

public class UserWriteTests
{
    [Fact]
    public void BuildRequest_produces_valid_userinfo_json()
    {
        var json = HikvisionUserWriteClient.BuildRequest(
            "489", "José \"Zé\" Silva",
            new DateTime(2026, 7, 21), new DateTime(2036, 7, 21));

        // Tem de ser JSON válido e conter os campos essenciais.
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var user = doc.RootElement.GetProperty("UserInfo");
        Assert.Equal("489", user.GetProperty("employeeNo").GetString());
        Assert.Equal("José \"Zé\" Silva", user.GetProperty("name").GetString()); // aspas escapadas
        Assert.True(user.GetProperty("Valid").GetProperty("enable").GetBoolean());
        Assert.Equal("2026-07-21T00:00:00", user.GetProperty("Valid").GetProperty("beginTime").GetString());
    }

    [Theory]
    [InlineData("{\"statusCode\":1,\"statusString\":\"OK\"}", true)]
    [InlineData("{\"statusCode\":6,\"statusString\":\"Invalid Content\"}", false)]
    [InlineData("{\"statusString\":\"OK\"}", true)]
    [InlineData("erro em html", false)]
    public void ResponseIsOk_detects_success(string json, bool expected)
    {
        Assert.Equal(expected, HikvisionUserWriteClient.ResponseIsOk(json));
    }
}
