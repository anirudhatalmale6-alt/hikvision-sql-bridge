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
        _log.Info($"Sincronização de utilizadores ligada (a cada {interval.TotalMinutes:0} min).");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var n = await SyncOnceAsync(ct);
                _log.Info($"Sincronização de utilizadores concluída ({n} utilizador(es)).");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.Error($"Sincronização de utilizadores: {ex.Message}"); }

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Faz uma sincronização completa de todos os terminais. Devolve o nº de utilizadores tratados.</summary>
    public async Task<int> SyncOnceAsync(CancellationToken ct)
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
                _log.Error($"Terminal {device.DisplayName} (sincronização): {ex.Message}");
            }
        }
        return total;
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

        // Regra do cliente: nunca alterar dados já inseridos. Só cria o que falta.
        var novoFuncionario = await _repo.InsertFuncionarioIfMissingAsync(idNumero, u.Name, ct);

        var tipos = MethodsToTipos(u).ToList();
        var novosIdentificadores = 0;
        foreach (var tipo in tipos)
            if (await _repo.InsertIdentificadorIfMissingAsync(idNumero, identificador, tipo, inicio, fim, ct))
                novosIdentificadores++;

        if (novoFuncionario || novosIdentificadores > 0)
        {
            _log.Info($"Utilizador criado/completado: {identificador} \"{u.Name}\" " +
                      $"(ficha nova: {(novoFuncionario ? "sim" : "não")}, identificadores novos: {novosIdentificadores} [{string.Join(",", tipos)}])");
            return true;
        }

        // Já existia tudo — não se mexeu em nada.
        return false;
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
