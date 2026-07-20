using HikvisionSqlBridge.Core.Hikvision;
using HikvisionSqlBridge.Core.Model;
using Xunit;

namespace HikvisionSqlBridge.Tests;

public class VerifyModeParserTests
{
    [Theory]
    [InlineData("face", VerifyMethod.Face)]
    [InlineData("fp", VerifyMethod.Fingerprint)]
    [InlineData("fingerPrint", VerifyMethod.Fingerprint)]
    [InlineData("card", VerifyMethod.Card)]
    [InlineData("QRCode", VerifyMethod.QrCode)]
    [InlineData("nfc", VerifyMethod.Nfc)]
    [InlineData("employeeNoAndPw", VerifyMethod.Pin)]
    [InlineData("cardOrFaceOrFp", VerifyMethod.Face)] // método específico primeiro
    [InlineData("", VerifyMethod.Unknown)]
    [InlineData("somethingElse", VerifyMethod.Unknown)]
    public void Parse_maps_hikvision_modes(string mode, VerifyMethod expected)
    {
        Assert.Equal(expected, VerifyModeParser.Parse(mode));
    }
}
