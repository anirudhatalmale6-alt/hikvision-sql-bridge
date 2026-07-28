using HikvisionSqlBridge.Core.Hikvision;
using HikvisionSqlBridge.Core.Model;
using Xunit;

namespace HikvisionSqlBridge.Tests;

/// <summary>
/// Montagem do plano de renumeração (quais os números com zeros à esquerda,
/// para onde vão, e se o destino já existe). Lógica pura, sem terminal.
/// </summary>
public class IdRenumberServiceTests
{
    private static List<TerminalUser> Sample() => new()
    {
        new TerminalUser { EmployeeNo = "00137", Name = "Bruno Henrique Costa Oliveira" }, // padded, destino existe
        new TerminalUser { EmployeeNo = "137",   Name = "Bruno Henrique Costa Oliveira" }, // destino vazio (sync)
        new TerminalUser { EmployeeNo = "00033", Name = "Luis Miguel Peniche Lopes" },     // padded, destino NAO existe
        new TerminalUser { EmployeeNo = "179",   Name = "Cristina" },                       // já sem zeros
    };

    [Fact]
    public void BuildPlan_PicksOnlyPadded_WithTargets()
    {
        var plan = IdRenumberService.BuildPlan(Sample());

        Assert.Equal(2, plan.Count);

        var bruno = plan.Single(p => p.OldNo == "00137");
        Assert.Equal("137", bruno.NewNo);
        Assert.True(bruno.TargetExists); // o 137 já existe (criado pela sync)

        var luis = plan.Single(p => p.OldNo == "00033");
        Assert.Equal("33", luis.NewNo);
        Assert.False(luis.TargetExists); // o 33 ainda não existe -> vai ser criado
    }

    [Fact]
    public void BuildPlan_NoPadded_Empty()
    {
        var users = new List<TerminalUser>
        {
            new() { EmployeeNo = "137" },
            new() { EmployeeNo = "83" },
        };
        Assert.Empty(IdRenumberService.BuildPlan(users));
    }

    [Fact]
    public void BuildPlan_OnlyId_ByOldNumber()
    {
        var plan = IdRenumberService.BuildPlan(Sample(), onlyId: "00137");
        var item = Assert.Single(plan);
        Assert.Equal("00137", item.OldNo);
        Assert.Equal("137", item.NewNo);
    }

    [Fact]
    public void BuildPlan_OnlyId_ByTargetNumber()
    {
        // Indicar "137" (o número sem zeros) também encontra o 00137.
        var plan = IdRenumberService.BuildPlan(Sample(), onlyId: "137");
        var item = Assert.Single(plan);
        Assert.Equal("00137", item.OldNo);
    }

    [Fact]
    public void BuildPlan_OnlyId_NoMatch_Empty()
    {
        Assert.Empty(IdRenumberService.BuildPlan(Sample(), onlyId: "99999"));
    }
}
