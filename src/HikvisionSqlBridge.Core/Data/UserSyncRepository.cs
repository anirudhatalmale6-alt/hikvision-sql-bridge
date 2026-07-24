using HikvisionSqlBridge.Core.Configuration;
using HikvisionSqlBridge.Core.Diagnostics;
using Microsoft.Data.SqlClient;

namespace HikvisionSqlBridge.Core.Data;

/// <summary>Um funcionário lido do SQL (TG_FUNCIONARIOS).</summary>
public readonly record struct FuncionarioRow(int IdNumero, string Nome);

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
    public async Task<bool> InsertFuncionarioIfMissingAsync(
        int idNumero, string nome, DateTime inicio, DateTime fim, CancellationToken ct = default)
    {
        var table = QuoteTable(_cfg.FuncionariosTable);
        var sql =
            $"IF NOT EXISTS (SELECT 1 FROM {table} WHERE ID_NUMERO = @numero) " +
            $"  INSERT INTO {table} " +
            $"    (ID_NUMERO, ID_NOME, ID_ACTIVO, ID_LAST_FASE_START, ID_LAST_FASE_END) " +
            $"  VALUES (@numero, @nome, 1, @inicio, @fim);";

        await using var conn = new SqlConnection(_sql.BuildConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@numero", System.Data.SqlDbType.Int).Value = idNumero;
        cmd.Parameters.Add("@nome", System.Data.SqlDbType.VarChar, 255).Value = (object?)nome ?? DBNull.Value;
        cmd.Parameters.Add("@inicio", System.Data.SqlDbType.DateTime).Value = inicio;
        cmd.Parameters.Add("@fim", System.Data.SqlDbType.DateTime).Value = fim;
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

    /// <summary>
    /// Lê a data de fim de validade de cada funcionário no SQL (ID_NUMERO -&gt;
    /// ID_FIM_VALIDADE da TA_IDENTIFICADORES). Quando um funcionário tem vários
    /// identificadores, usa a data mais tardia (todos deviam ter a mesma). Só
    /// entram os que têm data preenchida. Serve para a sincronização de validade.
    /// </summary>
    public async Task<Dictionary<int, DateTime>> ReadValidityEndsAsync(CancellationToken ct = default)
    {
        var table = QuoteTable(_cfg.IdentificadoresTable);
        var sql =
            $"SELECT ID_NUMERO, MAX(ID_FIM_VALIDADE) AS FIM FROM {table} " +
            $"WHERE ID_FIM_VALIDADE IS NOT NULL GROUP BY ID_NUMERO";

        var map = new Dictionary<int, DateTime>();
        await using var conn = new SqlConnection(_sql.BuildConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
            var id = Convert.ToInt32(reader.GetValue(0));
            map[id] = reader.GetDateTime(1);
        }
        return map;
    }

    /// <summary>
    /// Atualiza a data de fim de validade de um funcionário no SQL, nos dois
    /// campos que a representam: ID_FIM_VALIDADE (todos os identificadores desse
    /// ID_NUMERO na TA_IDENTIFICADORES) e ID_LAST_FASE_END (na TG_FUNCIONARIOS),
    /// mantendo-os coerentes. Devolve o nº de linhas alteradas.
    /// </summary>
    public async Task<int> UpdateValidityEndAsync(int idNumero, DateTime fim, CancellationToken ct = default)
    {
        var ident = QuoteTable(_cfg.IdentificadoresTable);
        var func = QuoteTable(_cfg.FuncionariosTable);
        var sql =
            $"UPDATE {ident} SET ID_FIM_VALIDADE = @fim WHERE ID_NUMERO = @numero; " +
            $"UPDATE {func}  SET ID_LAST_FASE_END = @fim WHERE ID_NUMERO = @numero;";

        await using var conn = new SqlConnection(_sql.BuildConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@fim", System.Data.SqlDbType.DateTime).Value = fim;
        cmd.Parameters.Add("@numero", System.Data.SqlDbType.Int).Value = idNumero;
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Lê os funcionários do SQL (para o sentido SQL -> terminais).</summary>
    public async Task<List<FuncionarioRow>> ReadFuncionariosAsync(CancellationToken ct = default)
    {
        var table = QuoteTable(_cfg.FuncionariosTable);
        var sql = $"SELECT ID_NUMERO, ID_NOME FROM {table}";

        var list = new List<FuncionarioRow>();
        await using var conn = new SqlConnection(_sql.BuildConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var idNumero = reader.GetInt32(0);
            var nome = reader.IsDBNull(1) ? "" : reader.GetString(1);
            list.Add(new FuncionarioRow(idNumero, nome));
        }
        return list;
    }

    private static string QuoteTable(string table)
    {
        var parts = table.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('.', parts.Select(p => "[" + p.Trim().Trim('[', ']').Replace("]", "]]") + "]"));
    }
}
