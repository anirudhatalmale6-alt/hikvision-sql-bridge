using HikvisionSqlBridge.Core;
using HikvisionSqlBridge.Core.Hikvision;
using HikvisionSqlBridge.Core.Model;
using Xunit;

namespace HikvisionSqlBridge.Tests;

public class UserSyncTests
{
    private const string UsersJson = @"{
      ""UserInfoSearch"": {
        ""searchID"": ""SIBHIK"",
        ""responseStatusStrg"": ""OK"",
        ""numOfMatches"": 2,
        ""totalMatches"": 2,
        ""UserInfo"": [
          { ""employeeNo"": ""1"", ""name"": ""Francisco Campos"", ""Valid"": { ""enable"": true, ""beginTime"": ""2023-03-23T00:00:00"", ""endTime"": ""2033-03-23T23:59:59"" }, ""numOfCard"": 0, ""numOfFP"": 1, ""numOfFace"": 1 },
          { ""employeeNo"": ""6"", ""name"": ""Julio"", ""numOfCard"": 2, ""numOfFP"": 0, ""numOfFace"": 1 }
        ]
      }
    }";

    [Fact]
    public void ParseResponse_reads_users_with_validity_and_methods()
    {
        var (users, status, count) = HikvisionUserInfoClient.ParseResponse(UsersJson);

        Assert.Equal("OK", status);
        Assert.Equal(2, count);
        Assert.Equal(2, users.Count);

        var francisco = users[0];
        Assert.Equal("1", francisco.EmployeeNo);
        Assert.Equal("Francisco Campos", francisco.Name);
        Assert.True(francisco.HasFingerprintOrFace);
        Assert.False(francisco.HasCard);
        Assert.Equal(new DateTime(2023, 3, 23), francisco.ValidBegin!.Value.Date);

        var julio = users[1];
        Assert.True(julio.HasCard);              // 2 cartões
        Assert.True(julio.HasFingerprintOrFace); // 1 face
    }

    [Fact]
    public void MethodsToTipos_maps_credentials_to_codes()
    {
        var cardAndFace = new TerminalUser { HasCard = true, HasFingerprintOrFace = true };
        Assert.Equal(new[] { 2, 1 }, UserSyncService.MethodsToTipos(cardAndFace).ToArray());

        var onlyCard = new TerminalUser { HasCard = true };
        Assert.Equal(new[] { 1 }, UserSyncService.MethodsToTipos(onlyCard).ToArray());

        // Sem informação => pelo menos digital/face (tipo 2).
        var nothing = new TerminalUser();
        Assert.Equal(new[] { 2 }, UserSyncService.MethodsToTipos(nothing).ToArray());
    }

    [Fact]
    public void ParseResponse_defaults_to_face_when_no_counts_present()
    {
        var json = @"{ ""UserInfoSearch"": { ""responseStatusStrg"": ""OK"", ""UserInfo"": [
            { ""employeeNo"": ""9"", ""name"": ""Sem contadores"" } ] } }";

        var (users, _, _) = HikvisionUserInfoClient.ParseResponse(json);

        Assert.Single(users);
        Assert.True(users[0].HasFingerprintOrFace); // fallback
        Assert.False(users[0].HasCard);
    }
}
