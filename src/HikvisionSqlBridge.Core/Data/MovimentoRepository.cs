using HikvisionSqlBridge.Core.Configuration;
using HikvisionSqlBridge.Core.Diagnostics;
using HikvisionSqlBridge.Core.Model;
using Microsoft.Data.SqlClient;

namespace HikvisionSqlBridge.Core.Data;

/// <summary>
/// Escreve as picagens na tabela de destino (por omissão TG_MOVIMENTOS).
/// O nome da tabela vem da configuração, por isso adapta-se a qualquer
/// instalação sem tocar no código.
/// </summary>
public sealed class MovimentoRepository
{
    private readonly SqlServerConfig _cfg;
    private readonly IAppLogger _log;

    public MovimentoRepository(SqlServerConfig cfg, IAppLogger log)
    {
        _cfg = cfg;
        _log = log;
    }

    /// <summary>Testa a ligação ao SQL Server (equivalente ao botão "Testar ligação").</summary>
    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqlConnection(_cfg.BuildConnectionString());
            await conn.OpenAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Falha a ligar ao SQL Server: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Insere uma picagem. Devolve true se gravou, false se era um duplicado
    /// (mesma chave primária) e foi ignorado sem erro.
    /// </summary>
    public async Task<bool> InsertAsync(Movimento m, CancellationToken ct = default)
    {
        var table = QuoteTable(_cfg.Table);
        var sql =
            $"INSERT INTO {table} " +
            "(ID_NUMERO, ID_DATAHORA, ID_MAIN_CODE, ID_TIPO_IDENTIFICADOR, ID_IDENTIFICADOR, " +
            " ID_TIPO, ID_SUPPORT_CODE, ID_IPTERMINAL, ID_END, ID_COD_CLASSFICACAO) " +
            "VALUES " +
            "(@numero, @datahora, @mainCode, @tipoIdent, @identificador, " +
            " @tipo, @supportCode, @ipTerminal, @end, @codClass)";

        await using var conn = new SqlConnection(_cfg.BuildConnectionString());
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@numero", System.Data.SqlDbType.Float).Value = m.IdNumero;
        cmd.Parameters.Add("@datahora", System.Data.SqlDbType.DateTime).Value = m.IdDataHora;
        cmd.Parameters.Add("@mainCode", System.Data.SqlDbType.Int).Value = m.IdMainCode;
        cmd.Parameters.Add("@tipoIdent", System.Data.SqlDbType.Int).Value = m.IdTipoIdentificador;
        cmd.Parameters.Add("@identificador", System.Data.SqlDbType.VarChar, 20).Value = m.IdIdentificador;
        cmd.Parameters.Add("@tipo", System.Data.SqlDbType.VarChar, 50).Value = m.IdTipo;
        cmd.Parameters.Add("@supportCode", System.Data.SqlDbType.VarChar, 50).Value = m.IdSupportCode;
        cmd.Parameters.Add("@ipTerminal", System.Data.SqlDbType.VarChar, 50).Value = m.IdIpTerminal;
        cmd.Parameters.Add("@end", System.Data.SqlDbType.Bit).Value = m.IdEnd;
        cmd.Parameters.Add("@codClass", System.Data.SqlDbType.VarChar, 8).Value = m.IdCodClassificacao;

        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (SqlException ex) when (IsDuplicateKey(ex))
        {
            // Chave primária (ID_NUMERO + ID_DATAHORA + ID_MAIN_CODE) já existe.
            // A picagem já lá estava — não é erro, apenas ignoramos.
            _log.Warn($"Picagem duplicada ignorada: utilizador {m.IdIdentificador} @ {m.IdDataHora:yyyy-MM-dd HH:mm:ss}");
            return false;
        }
    }

    private static bool IsDuplicateKey(SqlException ex)
    {
        foreach (SqlError e in ex.Errors)
            if (e.Number == 2627 || e.Number == 2601) // violação de PK / índice único
                return true;
        return false;
    }

    private static string QuoteTable(string table)
    {
        // Suporta "TG_MOVIMENTOS" ou "dbo.TG_MOVIMENTOS", protegendo cada parte
        // com parêntesis rectos.
        var parts = table.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('.', parts.Select(p => "[" + p.Trim().Trim('[', ']').Replace("]", "]]") + "]"));
    }
}
