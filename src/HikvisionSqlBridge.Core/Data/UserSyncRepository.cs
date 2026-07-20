using HikvisionSqlBridge.Core.Configuration;
using HikvisionSqlBridge.Core.Diagnostics;
using Microsoft.Data.SqlClient;

namespace HikvisionSqlBridge.Core.Data;

/// <summary>
/// Grava/atualiza os utilizadores no SQL: a ficha em TG_FUNCIONARIOS e os
/// identificadores em TA_IDENTIFICADORES. Os nomes das tabelas vêm da
/// configuração, por isso adapta-se a qualquer instalação.
/// </summary>
public sealed class UserSyncRepository
{
    private readonly SqlServerConfig _sql;
    private readonly UserSyncConfig _cfg;
    private readonly IAppLogger _log;

    public UserSyncRepository(SqlServerConfig sql, UserSyncConfig cfg, IAppLogger log)
    {
        _sql = sql;
        _cfg = cfg;
        _log = log;
    }

    /// <summary>
    /// Insere a ficha do funcionário SÓ SE ainda não existir. Se o ID_NUMERO já
    /// existir, não mexe em nada (não altera dados já inseridos). Devolve true se
    /// inseriu, false se já existia.
    /// </summary>
    public async Task<bool> InsertFuncionarioIfMissingAsync(int idNumero, string nome, CancellationToken ct = default)
    {
        var table = QuoteTable(_cfg.FuncionariosTable);
        var sql =
            $"IF NOT EXISTS (SELECT 1 FROM {table} WHERE ID_NUMERO = @numero) " +
            $"  INSERT INTO {table} (ID_NUMERO, ID_NOME, ID_ACTIVO) VALUES (@numero, @nome, 1);";

        await using var conn = new SqlConnection(_sql.BuildConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@numero", System.Data.SqlDbType.Int).Value = idNumero;
        cmd.Parameters.Add("@nome", System.Data.SqlDbType.VarChar, 255).Value = (object?)nome ?? DBNull.Value;
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    /// <summary>
    /// Insere um identificador (número + tipo) SÓ SE o par (ID_IDENTIFICADOR,
    /// ID_TIPO_IDENTIFICADOR) ainda não existir. Se já existir, não altera nada.
    /// Devolve true se inseriu, false se já existia.
    /// </summary>
    public async Task<bool> InsertIdentificadorIfMissingAsync(
        int idNumero, string identificador, int tipo, DateTime inicio, DateTime fim, CancellationToken ct = default)
    {
        var table = QuoteTable(_cfg.IdentificadoresTable);
        var sql =
            $"IF NOT EXISTS (SELECT 1 FROM {table} WHERE ID_IDENTIFICADOR = @ident AND ID_TIPO_IDENTIFICADOR = @tipo) " +
            $"  INSERT INTO {table} " +
            $"    (ID_NUMERO, ID_IDENTIFICADOR, ID_TIPO_IDENTIFICADOR, ID_FUNCAO, ID_INICIO_VALIDADE, ID_FIM_VALIDADE) " +
            $"  VALUES (@numero, @ident, @tipo, 0, @inicio, @fim);";

        await using var conn = new SqlConnection(_sql.BuildConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@numero", System.Data.SqlDbType.Int).Value = idNumero;
        cmd.Parameters.Add("@ident", System.Data.SqlDbType.VarChar, 20).Value = identificador;
        cmd.Parameters.Add("@tipo", System.Data.SqlDbType.Int).Value = tipo;
        cmd.Parameters.Add("@inicio", System.Data.SqlDbType.DateTime).Value = inicio;
        cmd.Parameters.Add("@fim", System.Data.SqlDbType.DateTime).Value = fim;
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    private static string QuoteTable(string table)
    {
        var parts = table.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('.', parts.Select(p => "[" + p.Trim().Trim('[', ']').Replace("]", "]]") + "]"));
    }
}
