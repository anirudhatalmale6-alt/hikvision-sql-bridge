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

    /// <summary>Insere a ficha do funcionário, ou atualiza o nome se já existir.</summary>
    public async Task UpsertFuncionarioAsync(int idNumero, string nome, CancellationToken ct = default)
    {
        var table = QuoteTable(_cfg.FuncionariosTable);
        var sql =
            $"IF EXISTS (SELECT 1 FROM {table} WHERE ID_NUMERO = @numero) " +
            $"  UPDATE {table} SET ID_NOME = @nome, ID_ACTIVO = 1 WHERE ID_NUMERO = @numero; " +
            $"ELSE " +
            $"  INSERT INTO {table} (ID_NUMERO, ID_NOME, ID_ACTIVO) VALUES (@numero, @nome, 1);";

        await using var conn = new SqlConnection(_sql.BuildConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@numero", System.Data.SqlDbType.Int).Value = idNumero;
        cmd.Parameters.Add("@nome", System.Data.SqlDbType.VarChar, 255).Value = (object?)nome ?? DBNull.Value;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Insere um identificador (número + tipo), ou atualiza o funcionário/validade
    /// se o par (ID_IDENTIFICADOR, ID_TIPO_IDENTIFICADOR) já existir.
    /// </summary>
    public async Task UpsertIdentificadorAsync(
        int idNumero, string identificador, int tipo, DateTime inicio, DateTime fim, CancellationToken ct = default)
    {
        var table = QuoteTable(_cfg.IdentificadoresTable);
        var sql =
            $"IF EXISTS (SELECT 1 FROM {table} WHERE ID_IDENTIFICADOR = @ident AND ID_TIPO_IDENTIFICADOR = @tipo) " +
            $"  UPDATE {table} SET ID_NUMERO = @numero, ID_INICIO_VALIDADE = @inicio, ID_FIM_VALIDADE = @fim " +
            $"  WHERE ID_IDENTIFICADOR = @ident AND ID_TIPO_IDENTIFICADOR = @tipo; " +
            $"ELSE " +
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
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string QuoteTable(string table)
    {
        var parts = table.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('.', parts.Select(p => "[" + p.Trim().Trim('[', ']').Replace("]", "]]") + "]"));
    }
}
