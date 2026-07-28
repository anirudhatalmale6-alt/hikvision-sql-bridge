using HikvisionSqlBridge.Core.Hikvision;
using Xunit;

namespace HikvisionSqlBridge.Tests;

/// <summary>
/// Regras de acerto dos números com zeros à esquerda (00137 -> 137), base da
/// migração das biometrias do ID antigo para o ID sem zeros.
/// </summary>
public class IdRenumberTests
{
    [Theory]
    [InlineData("00137", "137")]
    [InlineData("0137", "137")]
    [InlineData("007", "7")]
    [InlineData("00033", "33")]
    [InlineData("137", "137")]     // já sem zeros -> igual
    [InlineData("0", "0")]         // só zero -> fica "0"
    [InlineData("000", "0")]       // tudo zeros -> "0"
    [InlineData("", "")]
    [InlineData("  00137  ", "137")] // com espaços -> trim + strip
    [InlineData("A137", "A137")]   // não é só dígitos -> não tocar
    [InlineData("12B", "12B")]
    public void StripLeadingZeros_Cases(string input, string expected)
    {
        Assert.Equal(expected, IdRenumber.StripLeadingZeros(input));
    }

    [Fact]
    public void StripLeadingZeros_Null_ReturnsEmpty()
    {
        Assert.Equal("", IdRenumber.StripLeadingZeros(null));
    }

    [Theory]
    [InlineData("00137", true)]
    [InlineData("0137", true)]
    [InlineData("00033", true)]
    [InlineData("00", true)]       // "00" -> "0", há zeros a mais
    [InlineData("137", false)]     // já normalizado
    [InlineData("0", false)]       // nada para tirar
    [InlineData("", false)]
    [InlineData("A137", false)]    // não numérico
    [InlineData("0A", false)]      // não numérico
    public void IsZeroPadded_Cases(string input, bool expected)
    {
        Assert.Equal(expected, IdRenumber.IsZeroPadded(input));
    }

    [Fact]
    public void IsZeroPadded_Null_False()
    {
        Assert.False(IdRenumber.IsZeroPadded(null));
    }
}
