using HikvisionSqlBridge.Core.Mapping;
using HikvisionSqlBridge.Core.Model;
using Xunit;

namespace HikvisionSqlBridge.Tests;

public class MovimentoMapperTests
{
    [Theory]
    [InlineData(VerifyMethod.Card, 1)]
    [InlineData(VerifyMethod.Fingerprint, 2)]
    [InlineData(VerifyMethod.Face, 2)]
    [InlineData(VerifyMethod.Pin, 3)]
    [InlineData(VerifyMethod.LicensePlate, 5)]
    [InlineData(VerifyMethod.Nfc, 6)]
    [InlineData(VerifyMethod.QrCode, 7)]
    [InlineData(VerifyMethod.Unknown, 2)] // por omissão fica 2 (digital/face)
    public void ToTipoIdentificador_maps_method_to_code(VerifyMethod method, int expected)
    {
        Assert.Equal(expected, MovimentoMapper.ToTipoIdentificador(method));
    }

    [Theory]
    [InlineData("1", "00001")]
    [InlineData("489", "00489")]
    [InlineData("06226", "06226")]
    [InlineData("12345", "12345")]
    [InlineData("123456", "123456")] // mais de 5 dígitos mantém-se
    [InlineData("", "00000")]
    public void FormatIdentificador_pads_to_five_digits(string input, string expected)
    {
        Assert.Equal(expected, MovimentoMapper.FormatIdentificador(input));
    }

    [Fact]
    public void ToIdNumero_uses_user_number_to_keep_primary_key_unique()
    {
        Assert.Equal(6226d, MovimentoMapper.ToIdNumero("6226"));
        Assert.Equal(489d, MovimentoMapper.ToIdNumero("00489"));
        Assert.Equal(0d, MovimentoMapper.ToIdNumero(""));
    }

    [Fact]
    public void ToMovimento_produces_expected_fixed_values()
    {
        var e = new AccessEvent
        {
            EventTime = new DateTime(2025, 6, 21, 20, 0, 48),
            EmployeeNo = "489",
            Method = VerifyMethod.Face,
            TerminalIp = "10.4.1.15",
            Granted = true,
        };

        var m = MovimentoMapper.ToMovimento(e);

        Assert.Equal(489d, m.IdNumero);
        Assert.Equal(new DateTime(2025, 6, 21, 20, 0, 48), m.IdDataHora);
        Assert.Equal(0, m.IdMainCode);
        Assert.Equal(2, m.IdTipoIdentificador);
        Assert.Equal("00489", m.IdIdentificador);
        Assert.Equal("I", m.IdTipo);
        Assert.Equal("0", m.IdSupportCode);
        Assert.Equal("10.4.1.15", m.IdIpTerminal);
        Assert.False(m.IdEnd);
        Assert.Equal("0", m.IdCodClassificacao);
    }
}
