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
            $"    (ID_NUMERO, ID_NOME, ID_NOME_PROFISSIONAL, ID_ACTIVO, ID_LAST_FASE_START, ID_LAST_FASE_END) " +
            $"  VALUES (@numero, @nome, @nomeprof, 1, @inicio, @fim);";

        await using var conn = new SqlConnection(_sql.BuildConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@numero", System.Data.SqlDbType.Int).Value = idNumero;
        cmd.Parameters.Add("@nome", System.Data.SqlDbType.VarChar, 255).Value = (object?)nome ?? DBNull.Value;
        cmd.Parameters.Add("@nomeprof", System.Data.SqlDbType.VarChar, 255).Value = (object?)ProfessionalName(nome) ?? DBNull.Value;
        cmd.Parameters.Add("@inicio", System.Data.SqlDbType.DateTime).Value = inicio;
        cmd.Parameters.Add("@fim", System.Data.SqlDbType.DateTime).Value = fim;
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    /// <summary>
    /// "Nome profissional" = primeiro + último nome, a partir do nome completo.
    /// Ex.: "Julio Manuel Santos Lopes" -> "Julio Lopes"; "Julio Lopes" -> "Julio
    /// Lopes"; "Julio" -> "Julio". Vazio devolve vazio.
    /// </summary>
    internal static string ProfessionalName(string? nomeCompleto)
    {
        if (string.IsNullOrWhiteSpace(nomeCompleto)) return "";
        var parts = nomeCompleto.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 1 ? parts[0] : parts[0] + " " + parts[^1];
    }

    /// <summary>
    /// Diz se o nome profissional já é uma escolha válida: usa APENAS palavras que
    /// existem no nome completo (ID_NOME). Ex.: nome "Ana Sofia Ribeiro Martins" ->
    /// "Sofia Ribeiro" é válido (o responsável escolheu esse a mão), "Ana Martins"
    /// também; mas "Teste" ou vazio não são. Serve para respeitar as escolhas
    /// manuais e só mexer nas que estão vazias ou com algo que não é o nome.
    /// </summary>
    internal static bool ProfessionalNameUsesOnlyNameWords(string? prof, string? nomeCompleto)
    {
        if (string.IsNullOrWhiteSpace(prof) || string.IsNullOrWhiteSpace(nomeCompleto)) return false;
        var nameWords = new HashSet<string>(
            nomeCompleto.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);
        var profWords = prof.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return profWords.Length > 0 && profWords.All(w => nameWords.Contains(w));
    }

    /// <summary>
    /// Preenche o ID_NOME_PROFISSIONAL (primeiro + último nome do ID_NOME).
    /// Regra normal (overwriteExisting=false): só mexe quando está EM BRANCO ou
    /// quando o que lá está não é o nome (tem palavras que não existem no ID_NOME,
    /// ex.: "Teste"). Se o responsável tiver posto um nome válido — só com palavras
    /// do próprio nome, ex.: "Sofia Ribeiro" — NÃO lhe toca. Com overwriteExisting=
    /// true força primeiro+último em todos. Devolve o nº de linhas alteradas.
    /// </summary>
    public async Task<int> FillProfessionalNamesAsync(bool overwriteExisting, CancellationToken ct = default)
    {
        var table = QuoteTable(_cfg.FuncionariosTable);
        var sql =
            $"SELECT ID_NUMERO, ID_NOME, ID_NOME_PROFISSIONAL FROM {table} " +
            $"WHERE ID_NOME IS NOT NULL AND LTRIM(RTRIM(ID_NOME)) <> ''";

        var updates = new List<(int id, string prof)>();
        await using var conn = new SqlConnection(_sql.BuildConnectionString());
        await conn.OpenAsync(ct);
        await using (var cmd = new SqlCommand(sql, conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                var id = Convert.ToInt32(reader.GetValue(0));
                var nome = reader.GetString(1);
                var atual = reader.IsDBNull(2) ? "" : reader.GetString(2);

                // No modo normal, respeita uma escolha válida já feita à mão.
                if (!overwriteExisting && ProfessionalNameUsesOnlyNameWords(atual, nome))
                    continue;

                var prof = ProfessionalName(nome);
                if (!string.IsNullOrEmpty(prof) && !string.Equals(prof, atual?.Trim(), StringComparison.Ordinal))
                    updates.Add((id, prof));
            }
        }

        int done = 0;
        foreach (var (id, prof) in updates)
        {
            if (ct.IsCancellationRequested) break;
            var upd = $"UPDATE {table} SET ID_NOME_PROFISSIONAL = @prof WHERE ID_NUMERO = @numero";
            await using var cmd = new SqlCommand(upd, conn);
            cmd.Parameters.Add("@prof", System.Data.SqlDbType.VarChar, 255).Value = prof;
            cmd.Parameters.Add("@numero", System.Data.SqlDbType.Int).Value = id;
            done += await cmd.ExecuteNonQueryAsync(ct);
        }
        return done;
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
    /// ID_LAST_FASE_END da TG_FUNCIONARIOS). É este o campo que manda na validade
    /// (indicado pelo cliente): a TA_IDENTIFICADORES pode ter uma data "permanente"
    /// muito distante (ex.: 2090) que não representa a validade real e que os
    /// terminais nem sequer aceitam. Só entram os que têm data preenchida.
    /// </summary>
    public async Task<Dictionary<int, DateTime>> ReadValidityEndsAsync(CancellationToken ct = default)
    {
        var table = QuoteTable(_cfg.FuncionariosTable);
        var sql =
            $"SELECT ID_NUMERO, ID_LAST_FASE_END FROM {table} " +
            $"WHERE ID_LAST_FASE_END IS NOT NULL";

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
    /// Lê a data de INÍCIO de validade de cada funcionário no SQL (ID_NUMERO -&gt;
    /// ID_LAST_FASE_START da TG_FUNCIONARIOS). Serve para o terminal seguir também
    /// a data de início (não só o fim). Só entram os que têm data preenchida.
    /// </summary>
    public async Task<Dictionary<int, DateTime>> ReadValidityStartsAsync(CancellationToken ct = default)
    {
        var table = QuoteTable(_cfg.FuncionariosTable);
        var sql =
            $"SELECT ID_NUMERO, ID_LAST_FASE_START FROM {table} " +
            $"WHERE ID_LAST_FASE_START IS NOT NULL";

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
