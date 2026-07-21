using HikvisionSqlBridge.Core.Configuration;
using HikvisionSqlBridge.Core.Data;
using HikvisionSqlBridge.Core.Diagnostics;
using HikvisionSqlBridge.Core.Hikvision;
using HikvisionSqlBridge.Core.Mapping;
using HikvisionSqlBridge.Core.Model;

namespace HikvisionSqlBridge.Core;

/// <summary>
/// Fase 2: lê periodicamente os utilizadores inscritos nos terminais e cria/
/// atualiza a ficha em TG_FUNCIONARIOS e os identificadores em TA_IDENTIFICADORES.
/// Assim, ao inscrever no iVMS, o utilizador aparece automaticamente no SQL.
/// </summary>
public sealed class UserSyncService
{
    private readonly AppConfig _config;
    private readonly UserSyncRepository _repo;
    private readonly IAppLogger _log;

    public UserSyncService(AppConfig config, UserSyncRepository repo, IAppLogger log)
    {
        _config = config;
        _repo = repo;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _config.UserSync.IntervalMinutes));
        _log.Info($"Sincronização de utilizadores ligada (sentido: {_config.UserSync.Direction}, a cada {interval.TotalMinutes:0} min).");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_config.UserSync.DoImportToSql)
                {
                    var n = await ImportToSqlOnceAsync(ct);
                    _log.Info($"iVMS -> SQL: {n} utilizador(es) novo(s).");
                }
                if (_config.UserSync.DoExportToTerminal)
                {
                    var n = await ExportToTerminalOnceAsync(ct);
                    _log.Info($"SQL -> terminais: {n} utilizador(es) novo(s).");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.Error($"Sincronização de utilizadores: {ex.Message}"); }

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Sentido iVMS -> SQL: lê os utilizadores dos terminais e cria no SQL os que
    /// ainda não existem. Devolve o nº de utilizadores novos.
    /// </summary>
    public async Task<int> ImportToSqlOnceAsync(CancellationToken ct)
    {
        int total = 0;
        foreach (var device in _config.Equipamentos)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                using var client = new HikvisionUserInfoClient(device, _log);
                var users = await client.GetAllUsersAsync(ct);
                foreach (var u in users)
                {
                    if (ct.IsCancellationRequested) break;
                    if (await SyncUserAsync(u, ct)) total++;
                }
                _log.Info($"Terminal {device.DisplayName}: {users.Count} utilizador(es) lido(s).");
            }
            catch (Exception ex)
            {
                _log.Error($"Terminal {device.DisplayName} (iVMS->SQL): {ex.Message}");
            }
        }
        return total;
    }

    /// <summary>
    /// Sentido SQL -> terminais: lê os funcionários do SQL e cria nos terminais os
    /// que ainda lá não existem (não mexe nos que já existem). A biometria é
    /// inscrita depois no próprio terminal. Devolve o nº de utilizadores criados.
    /// </summary>
    public async Task<int> ExportToTerminalOnceAsync(CancellationToken ct)
    {
        var funcionarios = await _repo.ReadFuncionariosAsync(ct);
        int totalCreated = 0;

        foreach (var device in _config.Equipamentos)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                // Quem já existe no terminal (para não mexer nesses).
                using var reader = new HikvisionUserInfoClient(device, _log);
                var existing = await reader.GetAllUsersAsync(ct);
                var existingNos = new HashSet<string>(existing.Select(u => u.EmployeeNo.Trim()));

                using var writer = new HikvisionUserWriteClient(device, _log);
                var begin = DateTime.Today;
                var end = DateTime.Today.AddYears(Math.Max(1, _config.UserSync.ValidityYears));

                int created = 0;
                foreach (var f in funcionarios)
                {
                    if (ct.IsCancellationRequested) break;
                    var employeeNo = f.IdNumero.ToString();
                    if (existingNos.Contains(employeeNo)) continue; // já existe -> não faz nada
                    if (await writer.CreateUserAsync(employeeNo, f.Nome, begin, end, ct))
                    {
                        created++;
                        _log.Info($"Utilizador criado no terminal {device.DisplayName}: {employeeNo} \"{f.Nome}\"");
                    }
                }
                totalCreated += created;
                _log.Info($"Terminal {device.DisplayName}: {created} criado(s) de {funcionarios.Count} no SQL.");
            }
            catch (Exception ex)
            {
                _log.Error($"Terminal {device.DisplayName} (SQL->terminal): {ex.Message}");
            }
        }
        return totalCreated;
    }

    internal async Task<bool> SyncUserAsync(TerminalUser u, CancellationToken ct)
    {
        if (!int.TryParse(u.EmployeeNo.Trim(), out var idNumero))
        {
            _log.Warn($"Utilizador ignorado (employeeNo não numérico): {u.EmployeeNo}");
            return false;
        }

        var identificador = MovimentoMapper.FormatIdentificador(u.EmployeeNo);
        var inicio = u.ValidBegin ?? DateTime.Today;
        var fim = u.ValidEnd ?? DateTime.Today.AddYears(Math.Max(1, _config.UserSync.ValidityYears));

        // Regra do cliente: olhar só para o ID_NUMERO na TG_FUNCIONARIOS.
        // Se já existir -> não faz nada (não mexe em nada). Se não existir -> cria
        // a ficha E os identificadores.
        var novoFuncionario = await _repo.InsertFuncionarioIfMissingAsync(idNumero, u.Name, inicio, fim, ct);
        if (!novoFuncionario)
            return false; // ID_NUMERO já existe -> não faz nada

        var tipos = MethodsToTipos(u).ToList();
        foreach (var tipo in tipos)
            await _repo.InsertIdentificadorIfMissingAsync(idNumero, identificador, tipo, inicio, fim, ct);

        _log.Info($"Utilizador novo criado: {identificador} \"{u.Name}\" [{string.Join(",", tipos)}]");
        return true;
    }

    /// <summary>
    /// Converte os métodos do utilizador nos códigos ID_TIPO_IDENTIFICADOR:
    /// digital/face = 2, cartão = 1, PIN = 3. Se não houver informação, cria
    /// pelo menos o tipo 2 (digital/face), o mais comum nestes terminais.
    /// </summary>
    internal static IEnumerable<int> MethodsToTipos(TerminalUser u)
    {
        var tipos = new List<int>();
        if (u.HasFingerprintOrFace) tipos.Add(2);
        if (u.HasCard) tipos.Add(1);
        if (u.HasPin) tipos.Add(3);
        if (tipos.Count == 0) tipos.Add(2);
        return tipos;
    }
}
