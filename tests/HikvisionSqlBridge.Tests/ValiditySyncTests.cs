using HikvisionSqlBridge.Core;
using Xunit;

namespace HikvisionSqlBridge.Tests;

/// <summary>
/// Regras da sincronização da data de fim de validade (SQL &lt;-&gt; terminal).
/// Cobre os cenários que o cliente descreveu: dar saída (data no passado) e
/// prolongar (data mais tarde), nos dois sentidos, e o desempate quando os dois
/// lados mudam ao mesmo tempo.
/// </summary>
public class ValiditySyncTests
{
    private static readonly DateTime Today = new(2026, 7, 24);

    [Fact]
    public void AllSidesEqual_KeepsThatDate()
    {
        var d = new DateTime(2030, 1, 1);
        var target = UserSyncService.DecideTargetEnd(last: d, new[] { d, d }, Today);
        Assert.Equal(d.Date, target.Date);
    }

    [Fact]
    public void OnlySqlChanged_SqlWins_ExtendLater()
    {
        var last = new DateTime(2027, 1, 1);
        var sql = new DateTime(2030, 12, 31); // prolongado no SQL
        var term = last;                       // terminal ainda no valor antigo
        var target = UserSyncService.DecideTargetEnd(last, new[] { sql, term }, Today);
        Assert.Equal(sql.Date, target.Date);
    }

    [Fact]
    public void OnlyTerminalChanged_TerminalWins_ExtendLater()
    {
        var last = new DateTime(2027, 1, 1);
        var sql = last;
        var term = new DateTime(2031, 6, 30); // prolongado no terminal
        var target = UserSyncService.DecideTargetEnd(last, new[] { sql, term }, Today);
        Assert.Equal(term.Date, target.Date);
    }

    [Fact]
    public void OffboardInSql_PastDatePropagates()
    {
        // Cliente deu saída no SQL (data de ontem); terminal ainda com data futura.
        var last = new DateTime(2030, 1, 1);
        var sql = Today.AddDays(-1); // saída
        var term = last;
        var target = UserSyncService.DecideTargetEnd(last, new[] { sql, term }, Today);
        Assert.Equal(sql.Date, target.Date);
    }

    [Fact]
    public void OffboardOnTerminal_PastDatePropagatesToSql()
    {
        var last = new DateTime(2030, 1, 1);
        var sql = last;
        var term = Today; // saída hoje no terminal
        var target = UserSyncService.DecideTargetEnd(last, new[] { sql, term }, Today);
        Assert.Equal(term.Date, target.Date);
    }

    [Fact]
    public void BothChanged_PastDateWinsOverFuture()
    {
        // Conflito: SQL prolongou para o futuro, terminal deu saída (passado).
        var last = new DateTime(2027, 1, 1);
        var sql = new DateTime(2035, 1, 1);
        var term = Today.AddDays(-2); // saída
        var target = UserSyncService.DecideTargetEnd(last, new[] { sql, term }, Today);
        Assert.Equal(term.Date, target.Date); // a saída manda
    }

    [Fact]
    public void BothChangedFuture_LaterDateWins()
    {
        var last = new DateTime(2027, 1, 1);
        var sql = new DateTime(2030, 1, 1);
        var term = new DateTime(2032, 1, 1);
        var target = UserSyncService.DecideTargetEnd(last, new[] { sql, term }, Today);
        Assert.Equal(term.Date, target.Date); // fica a mais tardia
    }

    [Fact]
    public void BothChangedPast_EarliestWins()
    {
        // Ambos deram saída em datas diferentes -> fica a mais restritiva (mais antiga).
        var last = new DateTime(2030, 1, 1);
        var sql = Today.AddDays(-1);
        var term = Today.AddDays(-10);
        var target = UserSyncService.DecideTargetEnd(last, new[] { sql, term }, Today);
        Assert.Equal(term.Date, target.Date);
    }

    [Fact]
    public void NoHistory_DifferentDates_UsesTiebreak()
    {
        // Primeira vez (sem estado): datas diferentes -> desempate (aqui, ambas futuras -> mais tardia).
        var sql = new DateTime(2029, 1, 1);
        var term = new DateTime(2033, 1, 1);
        var target = UserSyncService.DecideTargetEnd(last: null, new[] { sql, term }, Today);
        Assert.Equal(term.Date, target.Date);
    }

    [Fact]
    public void TimeOfDayIgnored_ComparesByDate()
    {
        var sqlWithTime = new DateTime(2030, 5, 5, 0, 0, 0);
        var termWithTime = new DateTime(2030, 5, 5, 23, 59, 59);
        var target = UserSyncService.DecideTargetEnd(last: null, new[] { sqlWithTime, termWithTime }, Today);
        Assert.Equal(new DateTime(2030, 5, 5), target.Date);
    }

    [Fact]
    public void ThreeSides_OneChanged_Propagates()
    {
        // SQL + 2 terminais; só um terminal prolongou.
        var last = new DateTime(2027, 1, 1);
        var sql = last;
        var term1 = new DateTime(2034, 1, 1); // mudou
        var term2 = last;
        var target = UserSyncService.DecideTargetEnd(last, new[] { sql, term1, term2 }, Today);
        Assert.Equal(term1.Date, target.Date);
    }
}
