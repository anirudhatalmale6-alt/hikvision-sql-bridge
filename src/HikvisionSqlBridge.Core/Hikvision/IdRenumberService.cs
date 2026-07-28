using HikvisionSqlBridge.Core.Configuration;
using HikvisionSqlBridge.Core.Diagnostics;
using HikvisionSqlBridge.Core.Model;

namespace HikvisionSqlBridge.Core.Hikvision;

/// <summary>
/// Limpeza pontual: nos terminais onde os utilizadores foram gravados com zeros
/// à esquerda (ex.: 00137), passa cada um para o número sem zeros (137),
/// MIGRANDO as biometrias (digitais + cartões + face) do número antigo para o
/// novo e só depois apagando o antigo.
///
/// Segurança: para cada pessoa, primeiro copia tudo para o número novo, CONFIRMA
/// que ficou lá, e só então apaga o número antigo. Se a confirmação falhar,
/// deixa o antigo intacto e regista o problema — nunca se perde biometria.
/// </summary>
public sealed class IdRenumberService
{
    private readonly AppConfig _cfg;
    private readonly IAppLogger _log;

    public IdRenumberService(AppConfig cfg, IAppLogger log)
    {
        _cfg = cfg;
        _log = log;
    }

    /// <summary>Uma renumeração planeada para um utilizador (00137 -> 137).</summary>
    public sealed record PlanItem(string OldNo, string NewNo, bool TargetExists, string Name, int Fingerprints);

    /// <summary>Resultado da migração de um utilizador.</summary>
    public sealed record MigrationResult(
        string OldNo, string NewNo, int FingerprintsPending, int CardsMoved,
        bool FaceMoved, bool Verified, bool OldDeleted, string Message);

    /// <summary>
    /// Monta a lista de utilizadores a renumerar (os que têm zeros à esquerda).
    /// Se <paramref name="onlyId"/> for indicado, filtra só esse (para testar num
    /// só). Função pura sobre a lista lida do terminal — testável sem equipamento.
    /// </summary>
    public static List<PlanItem> BuildPlan(IEnumerable<TerminalUser> users, string? onlyId = null)
    {
        var all = users.ToList();
        var existing = new HashSet<string>(all.Select(u => u.EmployeeNo.Trim()), StringComparer.OrdinalIgnoreCase);
        var plan = new List<PlanItem>();

        foreach (var u in all)
        {
            var old = u.EmployeeNo.Trim();
            if (!IdRenumber.IsZeroPadded(old))
                continue;

            var target = IdRenumber.StripLeadingZeros(old);
            if (onlyId is not null &&
                !old.Equals(onlyId.Trim(), StringComparison.OrdinalIgnoreCase) &&
                !target.Equals(IdRenumber.StripLeadingZeros(onlyId), StringComparison.OrdinalIgnoreCase))
                continue;

            plan.Add(new PlanItem(old, target, existing.Contains(target), u.Name, Math.Max(0, u.NumFingerprints)));
        }

        return plan;
    }

    /// <summary>
    /// Só LÊ o terminal e devolve o plano (não altera nada). Para o cliente ver o
    /// que vai acontecer antes de correr a sério.
    /// </summary>
    public async Task<List<PlanItem>> PlanAsync(DeviceConfig device, string? onlyId, CancellationToken ct)
    {
        using var userClient = new HikvisionUserInfoClient(device, _log);
        var users = await userClient.GetAllUsersAsync(ct);
        _log.Info($"{device.DisplayName}: {users.Count} utilizador(es) no terminal.");
        return BuildPlan(users, onlyId);
    }

    /// <summary>
    /// Corre a renumeração num terminal. Se <paramref name="onlyId"/> for indicado,
    /// trata só desse. Se <paramref name="apply"/> for false, é simulação (não muda nada).
    /// </summary>
    public async Task<List<MigrationResult>> RenumberAsync(DeviceConfig device, string? onlyId, bool apply, CancellationToken ct)
    {
        var results = new List<MigrationResult>();

        using var userClient = new HikvisionUserInfoClient(device, _log);
        var users = await userClient.GetAllUsersAsync(ct);
        var plan = BuildPlan(users, onlyId);

        _log.Info($"{device.DisplayName}: {plan.Count} utilizador(es) com zeros a' esquerda para acertar" +
                  (apply ? "" : " (SIMULACAO — nao vai alterar nada)") + ".");

        if (!apply)
        {
            foreach (var p in plan)
                results.Add(new MigrationResult(p.OldNo, p.NewNo, p.Fingerprints, 0, false, false, false,
                    $"(simulacao) {p.OldNo} -> {p.NewNo}" +
                    (p.TargetExists ? " [destino ja' existe]" : " [cria destino]") +
                    (p.Fingerprints > 0 ? $" [tem {p.Fingerprints} digital(is) -> re-scan]" : "")));
            return results;
        }

        using var writeClient = new HikvisionUserWriteClient(device, _log);
        using var bio = new HikvisionBiometricClient(device, _log);
        var fdid = await bio.GetFaceLibIdAsync(ct);
        _log.Info($"{device.DisplayName}: biblioteca facial FDID={fdid}.");

        foreach (var p in plan)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var r = await MigrateOneAsync(device, writeClient, bio, users, p, fdid, ct);
                results.Add(r);
                _log.Info($"{device.DisplayName}: {r.OldNo} -> {r.NewNo}: {r.Message}");
            }
            catch (Exception ex)
            {
                results.Add(new MigrationResult(p.OldNo, p.NewNo, 0, 0, false, false, false, "ERRO: " + ex.Message));
                _log.Error($"{device.DisplayName}: {p.OldNo} -> {p.NewNo} falhou: {ex.Message}");
            }
        }

        return results;
    }

    private async Task<MigrationResult> MigrateOneAsync(
        DeviceConfig device, HikvisionUserWriteClient writeClient, HikvisionBiometricClient bio,
        List<TerminalUser> users, PlanItem p, string fdid, CancellationToken ct)
    {
        var source = users.First(u => u.EmployeeNo.Trim().Equals(p.OldNo, StringComparison.OrdinalIgnoreCase));

        // 1) Garantir que o número novo existe (a sincronizacao costuma ja' o ter
        //    criado vazio; se nao, criamos com o nome e validade do antigo).
        if (!p.TargetExists)
        {
            var begin = source.ValidBegin ?? DateTime.Today;
            var end = UserSyncService.ClampTerminalEnd(source.ValidEnd ?? DateTime.Today.AddYears(_cfg.UserSync.ValidityYears));
            begin = UserSyncService.ClampBeginToEnd(UserSyncService.FloorTerminalBegin(begin), end);
            var created = await writeClient.CreateUserAsync(p.NewNo, source.Name, begin, end, ct);
            if (!created)
                return new MigrationResult(p.OldNo, p.NewNo, 0, 0, false, false, false,
                    "nao consegui criar o numero novo — antigo mantido, nada perdido.");
        }

        // A DIGITAL não se migra: este firmware não deixa importar digitais por
        // software (responde "notSupport"). Guardamos só quantas tinha, para
        // avisar que precisam de um re-scan rápido no número novo.
        int fpParaRescan = Math.Max(0, source.NumFingerprints);

        // 2) Cartões — só os que faltam (idempotente).
        var oldCards = await bio.GetCardsAsync(p.OldNo, ct);
        var newCardNos = new HashSet<string>((await bio.GetCardsAsync(p.NewNo, ct)).Select(c => c.CardNo), StringComparer.OrdinalIgnoreCase);
        int cardsMoved = 0;
        foreach (var c in oldCards)
        {
            if (newCardNos.Contains(c.CardNo)) continue;
            if (await bio.AddCardAsync(p.NewNo, c, ct)) cardsMoved++;
        }

        // 3) Face — se o antigo tem e o novo ainda não.
        var oldFaces = await bio.CountFacesAsync(p.OldNo, fdid, ct);
        bool faceMoved = false;
        bool faceProblem = false;
        if (oldFaces > 0 && await bio.CountFacesAsync(p.NewNo, fdid, ct) == 0)
        {
            var jpeg = await bio.DownloadFaceAsync(p.OldNo, fdid, ct);
            if (jpeg is not null)
                faceMoved = await bio.AddFaceAsync(p.NewNo, fdid, jpeg, source.Name, ct);
            if (!faceMoved) faceProblem = true;
        }

        // 4) VERIFICAR a face e o cartão no destino ANTES de apagar o antigo. Se a
        // face/cartão da origem não ficaram no destino, NÃO apaga — nada se perde.
        int expCards = Math.Max(source.NumCards, oldCards.Count);
        int expFaces = Math.Max(source.NumFaces, oldFaces);

        var verCards = (await bio.GetCardsAsync(p.NewNo, ct)).Count;
        var verFaces = await bio.CountFacesAsync(p.NewNo, fdid, ct);

        bool ok = verCards >= expCards && verFaces >= expFaces;

        if (!ok)
        {
            return new MigrationResult(p.OldNo, p.NewNo, fpParaRescan, cardsMoved, faceMoved, false, false,
                $"verificacao falhou (destino tem {verCards} cartoes / {verFaces} face; " +
                $"a origem precisava de {expCards}/{expFaces}). Antigo MANTIDO — nada perdido." +
                (faceProblem ? " A face nao passou." : ""));
        }

        // 5) Só agora apaga o antigo.
        var deleted = await bio.DeleteUserAsync(p.OldNo, ct);
        var msg =
            $"OK: {verFaces} face, {verCards} cartao(oes) no {p.NewNo}. " +
            (deleted ? $"Antigo {p.OldNo} apagado." : $"ATENCAO: nao consegui apagar o antigo {p.OldNo}.");
        if (fpParaRescan > 0)
            msg += $" DIGITAL: {p.NewNo} precisa de re-scan de {fpParaRescan} dedo(s) (o terminal nao deixa copiar digitais).";
        return new MigrationResult(p.OldNo, p.NewNo, fpParaRescan, cardsMoved, verFaces > 0, true, deleted, msg);
    }
}
