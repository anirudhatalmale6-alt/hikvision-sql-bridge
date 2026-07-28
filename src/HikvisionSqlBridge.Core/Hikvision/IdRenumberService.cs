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
    public sealed record PlanItem(string OldNo, string NewNo, bool TargetExists, string Name);

    /// <summary>Resultado da migração de um utilizador.</summary>
    public sealed record MigrationResult(
        string OldNo, string NewNo, int FingerprintsMoved, int CardsMoved,
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

            plan.Add(new PlanItem(old, target, existing.Contains(target), u.Name));
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
                results.Add(new MigrationResult(p.OldNo, p.NewNo, 0, 0, false, false, false,
                    $"(simulacao) {p.OldNo} -> {p.NewNo}" + (p.TargetExists ? " [destino ja' existe]" : " [cria destino]")));
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

        // Estado de origem. Se o terminal já diz que este utilizador não tem
        // digitais (numOfFP=0), poupamos as 10..20 consultas por dedo.
        var oldFps = source.NumFingerprints == 0
            ? new List<HikvisionBiometricClient.FingerTemplate>()
            : await bio.DownloadFingerprintsAsync(p.OldNo, ct);
        var oldCards = await bio.GetCardsAsync(p.OldNo, ct);
        var oldFaces = await bio.CountFacesAsync(p.OldNo, fdid, ct);

        // 2) Digitais — só as que ainda faltam no destino (idempotente).
        var newFpIds = new HashSet<int>((await bio.DownloadFingerprintsAsync(p.NewNo, ct)).Select(f => f.FingerPrintId));
        int fpMoved = 0;
        foreach (var fp in oldFps)
        {
            if (newFpIds.Contains(fp.FingerPrintId)) continue;
            if (await bio.UploadFingerprintAsync(p.NewNo, fp, ct)) fpMoved++;
        }

        // 3) Cartões — só os que faltam.
        var newCardNos = new HashSet<string>((await bio.GetCardsAsync(p.NewNo, ct)).Select(c => c.CardNo), StringComparer.OrdinalIgnoreCase);
        int cardsMoved = 0;
        foreach (var c in oldCards)
        {
            if (newCardNos.Contains(c.CardNo)) continue;
            if (await bio.AddCardAsync(p.NewNo, c, ct)) cardsMoved++;
        }

        // 4) Face — se o antigo tem e o novo ainda nao.
        bool faceMoved = false;
        bool faceProblem = false;
        if (oldFaces > 0 && await bio.CountFacesAsync(p.NewNo, fdid, ct) == 0)
        {
            var jpeg = await bio.DownloadFaceAsync(p.OldNo, fdid, ct);
            if (jpeg is not null)
                faceMoved = await bio.AddFaceAsync(p.NewNo, fdid, jpeg, source.Name, ct);
            if (!faceMoved) faceProblem = true;
        }

        // 5) VERIFICAR o destino antes de apagar o antigo. O que TEM de estar no
        // destino é o que conseguimos LER da origem. Para cartões e face usamos
        // também o contador que o terminal declara (mais seguro). Para as digitais
        // usamos o que lemos mesmo (o contador da lista às vezes conta a mais),
        // mas com uma salvaguarda: se o terminal diz que há digitais e nós não
        // conseguimos ler nenhuma, é sinal de falha de leitura -> NÃO apaga.
        int expFps = oldFps.Count;
        int expCards = Math.Max(source.NumCards, oldCards.Count);
        int expFaces = Math.Max(source.NumFaces, oldFaces);
        bool fpReadSuspeita = source.NumFingerprints > 0 && oldFps.Count == 0;

        var verFps = (await bio.DownloadFingerprintsAsync(p.NewNo, ct)).Count;
        var verCards = (await bio.GetCardsAsync(p.NewNo, ct)).Count;
        var verFaces = await bio.CountFacesAsync(p.NewNo, fdid, ct);

        bool ok = !fpReadSuspeita && verFps >= expFps && verCards >= expCards && verFaces >= expFaces;

        if (!ok)
        {
            return new MigrationResult(p.OldNo, p.NewNo, fpMoved, cardsMoved, faceMoved, false, false,
                $"verificacao falhou (destino tem {verFps} digitais / {verCards} cartoes / {verFaces} face; " +
                $"a origem precisava de {expFps}/{expCards}/{expFaces}). Antigo MANTIDO — nada perdido." +
                (fpReadSuspeita ? $" (o terminal diz que o {p.OldNo} tem {source.NumFingerprints} digital(is) mas nao consegui le-las)" : "") +
                (faceProblem ? " A face nao passou." : ""));
        }

        // 6) Só agora apaga o antigo.
        var deleted = await bio.DeleteUserAsync(p.OldNo, ct);
        return new MigrationResult(p.OldNo, p.NewNo, fpMoved, cardsMoved, verFaces > 0, true, deleted,
            $"OK: {verFps} digital(is), {verCards} cartao(oes), {verFaces} face no {p.NewNo}. " +
            (deleted ? $"Antigo {p.OldNo} apagado." : $"ATENCAO: nao consegui apagar o antigo {p.OldNo} (biometrias ja' estao no {p.NewNo})."));
    }
}
