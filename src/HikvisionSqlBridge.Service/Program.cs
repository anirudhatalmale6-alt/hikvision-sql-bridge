using System.Text.Json;
using HikvisionSqlBridge.Core;
using HikvisionSqlBridge.Core.Configuration;
using HikvisionSqlBridge.Core.Data;
using HikvisionSqlBridge.Core.Diagnostics;
using HikvisionSqlBridge.Core.Mapping;
using HikvisionSqlBridge.Core.Model;
using HikvisionSqlBridge.Service;

// Carrega a configuração a partir de config.json (ao lado do executável).
// Este é o mesmo ficheiro que a janela de configuração grava, por isso a
// ferramenta funciona em qualquer servidor só editando/gerando este ficheiro.
var configPath = Path.Combine(ExeDirectory(), "config.json");
var appConfig = LoadConfig(configPath);
// Os ficheiros de estado (ex.: validade) ficam ao lado do config.json.
appConfig.BaseDirectory = Path.GetDirectoryName(configPath) ?? ExeDirectory();

// --------- Modos de teste por linha de comandos (não instalam serviço) ---------
// Permitem validar a parte de SQL sem precisar de um terminal Hikvision.
if (args.Length > 0)
{
    switch (args[0].ToLowerInvariant())
    {
        case "--test-connection":
            return await RunTestConnection(appConfig);

        case "--simulate":
            // --simulate <employeeNo> [metodo]
            // metodo: card | fp | face | pin | nfc | qr | plate  (por omissão: face)
            return await RunSimulate(appConfig, args);

        case "--sync-users":
            // Sentido iVMS -> SQL: lê os utilizadores dos terminais e grava no SQL.
            return await RunSyncUsers(appConfig);

        case "--export-users":
            // Sentido SQL -> terminais: cria nos terminais os utilizadores do SQL.
            return await RunExportUsers(appConfig);

        case "--sync-validity":
            // Sincroniza uma vez as alterações da data de fim de validade (SQL <-> terminais).
            return await RunSyncValidity(appConfig);

        case "--nomes-profissionais":
            // Preenche o ID_NOME_PROFISSIONAL (primeiro + último nome) na TG_FUNCIONARIOS.
            // "todos" como 2º argumento -> também substitui os que já têm algo escrito.
            return await RunFillProfessionalNames(appConfig, args);

        case "--renumerar":
            // Limpeza pontual: tira os zeros à esquerda dos números no terminal
            // (00137 -> 137) migrando as biometrias (digitais + cartões + face).
            //   sem 2º arg   -> só mostra o plano (não altera nada).
            //   plano        -> igual (simulação explícita).
            //   <employeeNo> -> migra só esse número (ex.: 00137, para testar).
            //   todos        -> migra todos os que têm zeros à esquerda.
            return await RunRenumber(appConfig, args);

        case "--diag-fp":
            // Diagnóstico: descobre como este terminal deixa LER as digitais.
            return await RunDiagFingerprint(appConfig, args);

        case "--config":
            // Abre a janela de configuração gráfica (no browser, servidor local).
            return await ConfigWebApp.RunAsync(configPath);

        case "--help":
        case "-h":
            PrintHelp();
            return 0;
    }
}

var builder = Host.CreateApplicationBuilder(args);

// Permite instalar/correr como Serviço do Windows.
builder.Services.AddWindowsService(o => o.ServiceName = "SIBHIK");

IAppLogger fileLogger = new FileAppLogger(appConfig.Logging);
builder.Services.AddSingleton(appConfig);
builder.Services.AddSingleton(fileLogger);
builder.Services.AddHostedService<BridgeWorker>();

var host = builder.Build();
host.Run();
return 0;

// ----------------------------- Modos de teste -----------------------------

static async Task<int> RunTestConnection(AppConfig cfg)
{
    var log = new ConsoleAppLogger();
    var repo = new MovimentoRepository(cfg.SqlServer, log);
    Console.WriteLine($"A testar ligação a {cfg.SqlServer.Server} / {cfg.SqlServer.Database} ...");
    var ok = await repo.TestConnectionAsync();
    Console.WriteLine(ok ? "Ligação OK." : "FALHOU. Ver mensagem acima.");
    return ok ? 0 : 1;
}

static async Task<int> RunSimulate(AppConfig cfg, string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Uso: SIBHIK.exe --simulate <employeeNo> [metodo]");
        Console.WriteLine("metodo: card | fp | face | pin | nfc | qr | plate  (por omissao: face)");
        return 2;
    }

    var employeeNo = args[1];
    var method = args.Length >= 3 ? ParseMethod(args[2]) : VerifyMethod.Face;
    var terminalIp = cfg.Equipamentos.Count > 0 ? cfg.Equipamentos[0].Ip : "0.0.0.0";

    var log = new ConsoleAppLogger();
    var repo = new MovimentoRepository(cfg.SqlServer, log);

    var ev = new AccessEvent
    {
        EventTime = DateTime.Now,
        EmployeeNo = employeeNo,
        Method = method,
        TerminalIp = terminalIp,
        Granted = true,
    };
    var mov = MovimentoMapper.ToMovimento(ev);

    Console.WriteLine("A inserir uma picagem de teste em " +
        $"{cfg.SqlServer.Database}.{cfg.SqlServer.Table}:");
    Console.WriteLine($"  ID_DATAHORA           = {mov.IdDataHora:yyyy-MM-dd HH:mm:ss}");
    Console.WriteLine($"  ID_TIPO_IDENTIFICADOR = {mov.IdTipoIdentificador}  ({method})");
    Console.WriteLine($"  ID_IDENTIFICADOR      = {mov.IdIdentificador}");
    Console.WriteLine($"  ID_IPTERMINAL         = {mov.IdIpTerminal}");
    Console.WriteLine($"  ID_NUMERO             = 0  (o trigger vai preenche-lo a partir de TA_IDENTIFICADORES)");

    var inserted = await repo.InsertAsync(mov);
    if (inserted)
    {
        Console.WriteLine("Inserido com sucesso. Confirme na tabela que o ID_NUMERO foi preenchido pelo trigger.");
        return 0;
    }

    Console.WriteLine("Nao inserido (picagem duplicada com a mesma chave, ou erro acima).");
    return 1;
}

static async Task<int> RunSyncUsers(AppConfig cfg)
{
    var log = new ConsoleAppLogger();
    if (cfg.Equipamentos.Count == 0)
    {
        Console.WriteLine("Nenhum terminal configurado na secção Equipamentos.");
        return 2;
    }

    var repo = new UserSyncRepository(cfg.SqlServer, cfg.UserSync, log);
    var sync = new UserSyncService(cfg, repo, log);

    Console.WriteLine("iVMS -> SQL: a ler os utilizadores dos terminais e a gravar no SQL...");
    Console.WriteLine($"  Funcionários  -> {cfg.UserSync.FuncionariosTable}");
    Console.WriteLine($"  Identificadores -> {cfg.UserSync.IdentificadoresTable}");
    var n = await sync.ImportToSqlOnceAsync(CancellationToken.None);
    Console.WriteLine($"Concluído. {n} utilizador(es) novo(s) no SQL.");
    return 0;
}

static async Task<int> RunExportUsers(AppConfig cfg)
{
    var log = new ConsoleAppLogger();
    if (cfg.Equipamentos.Count == 0)
    {
        Console.WriteLine("Nenhum terminal configurado na secção Equipamentos.");
        return 2;
    }

    var repo = new UserSyncRepository(cfg.SqlServer, cfg.UserSync, log);
    var sync = new UserSyncService(cfg, repo, log);

    Console.WriteLine("SQL -> terminais: a ler os funcionários do SQL e a criá-los nos terminais...");
    Console.WriteLine("(Só cria os que ainda não existem no terminal. A biometria inscreve-se depois no equipamento.)");
    var n = await sync.ExportToTerminalOnceAsync(CancellationToken.None);
    Console.WriteLine($"Concluído. {n} utilizador(es) criado(s) nos terminais.");
    return 0;
}

static async Task<int> RunSyncValidity(AppConfig cfg)
{
    var log = new ConsoleAppLogger();
    if (cfg.Equipamentos.Count == 0)
    {
        Console.WriteLine("Nenhum terminal configurado na secção Equipamentos.");
        return 2;
    }

    var repo = new UserSyncRepository(cfg.SqlServer, cfg.UserSync, log);
    var sync = new UserSyncService(cfg, repo, log);

    Console.WriteLine("Sincronização de validade (SQL <-> terminais): a comparar datas de fim...");
    var n = await sync.SyncValidityOnceAsync(CancellationToken.None);
    Console.WriteLine($"Concluído. {n} funcionário(s) com validade atualizada.");
    return 0;
}

static async Task<int> RunFillProfessionalNames(AppConfig cfg, string[] args)
{
    var log = new ConsoleAppLogger();
    var overwrite = args.Length >= 2 && args[1].Equals("todos", StringComparison.OrdinalIgnoreCase);

    var repo = new UserSyncRepository(cfg.SqlServer, cfg.UserSync, log);
    Console.WriteLine("A preencher o ID_NOME_PROFISSIONAL (primeiro + último nome) na " +
        $"{cfg.UserSync.FuncionariosTable}...");
    Console.WriteLine(overwrite
        ? "Modo: TODOS (substitui também os que já têm algo escrito)."
        : "Modo: só os que estão vazios (não mexe nos que já têm algo).");

    var n = await repo.FillProfessionalNamesAsync(overwrite, CancellationToken.None);
    Console.WriteLine($"Concluído. {n} funcionário(s) atualizado(s).");
    return 0;
}

static async Task<int> RunRenumber(AppConfig cfg, string[] args)
{
    var log = new ConsoleAppLogger();
    if (cfg.Equipamentos.Count == 0)
    {
        Console.WriteLine("Nenhum terminal configurado na secção Equipamentos.");
        return 2;
    }

    // Interpretar o 2º argumento.
    string? onlyId = null;
    bool apply;
    var arg = args.Length >= 2 ? args[1].Trim() : "";
    if (arg.Length == 0 || arg.Equals("plano", StringComparison.OrdinalIgnoreCase))
    {
        apply = false; // simulação / plano
    }
    else if (arg.Equals("todos", StringComparison.OrdinalIgnoreCase))
    {
        apply = true;  // migrar todos
    }
    else
    {
        apply = true;  // migrar só este número
        onlyId = arg;
    }

    Console.WriteLine("SIBHIK v0.4.11 — acerto de números com zeros à esquerda (00137 -> 137)");
    Console.WriteLine(apply
        ? (onlyId is null
            ? "Modo: MIGRAR TODOS (copia FACE + CARTÃO para o número sem zeros e apaga o antigo)."
            : $"Modo: MIGRAR SÓ o número {onlyId}.")
        : "Modo: PLANO (só mostra o que vai acontecer — não altera nada).");
    Console.WriteLine("Nota: a impressão digital não se copia (o terminal não deixa); quem usa dedo");
    Console.WriteLine("      faz um re-scan rápido no número novo. A FACE e o CARTÃO migram sozinhos.");
    Console.WriteLine();

    var svc = new HikvisionSqlBridge.Core.Hikvision.IdRenumberService(cfg, log);
    int totalPadded = 0, totalDeleted = 0, totalKept = 0;
    var precisamRescan = new List<string>();

    foreach (var device in cfg.Equipamentos)
    {
        Console.WriteLine($"== Terminal {device.DisplayName} ==");
        List<HikvisionSqlBridge.Core.Hikvision.IdRenumberService.MigrationResult> results;
        try
        {
            results = await svc.RenumberAsync(device, onlyId, apply, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Não consegui falar com este terminal: {ex.Message}");
            Console.WriteLine("  (Verifique o IP/porta/utilizador/palavra-passe em config.json e a ligação de rede.)");
            Console.WriteLine();
            continue;
        }
        if (results.Count == 0)
        {
            Console.WriteLine("  Nenhum número com zeros à esquerda encontrado.");
            Console.WriteLine();
            continue;
        }

        foreach (var r in results)
        {
            totalPadded++;
            if (apply && r.OldDeleted) totalDeleted++;
            else if (apply) totalKept++;
            if (r.FingerprintsPending > 0)
                precisamRescan.Add($"{r.NewNo} ({r.FingerprintsPending} dedo(s))");
            Console.WriteLine($"  {r.OldNo} -> {r.NewNo}: {r.Message}");
        }
        Console.WriteLine();
    }

    if (!apply)
    {
        Console.WriteLine($"Plano: {totalPadded} número(s) com zeros à esquerda.");
        if (precisamRescan.Count > 0)
            Console.WriteLine($"Destes, {precisamRescan.Count} usam digital e vão precisar de re-scan: {string.Join(", ", precisamRescan)}");
        Console.WriteLine("Para migrar só um (teste): SIBHIK.exe --renumerar 00137");
        Console.WriteLine("Para migrar todos:        SIBHIK.exe --renumerar todos");
    }
    else
    {
        Console.WriteLine($"Concluído. {totalDeleted} migrado(s) (FACE + CARTÃO) e antigo apagado; {totalKept} mantido(s) (ver avisos acima).");
        if (precisamRescan.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"RE-SCAN DA DIGITAL — estes {precisamRescan.Count} número(s) tinham digital e precisam de a re-inscrever no terminal:");
            Console.WriteLine("  " + string.Join(", ", precisamRescan));
        }
    }
    return 0;
}

static async Task<int> RunDiagFingerprint(AppConfig cfg, string[] args)
{
    var log = new ConsoleAppLogger();
    if (cfg.Equipamentos.Count == 0)
    {
        Console.WriteLine("Nenhum terminal configurado na secção Equipamentos.");
        return 2;
    }
    var employeeNo = args.Length >= 2 ? args[1].Trim() : "00137";

    Console.WriteLine("SIBHIK v0.4.6 — diagnóstico das impressões digitais");
    Console.WriteLine($"A perguntar ao terminal como se leem as digitais do {employeeNo}...");
    Console.WriteLine();

    foreach (var device in cfg.Equipamentos)
    {
        Console.WriteLine($"== Terminal {device.DisplayName} ==");
        using var bio = new HikvisionSqlBridge.Core.Hikvision.HikvisionBiometricClient(device, log);
        try
        {
            await bio.DiagnoseFingerprintAsync(employeeNo, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Falha a falar com o terminal: {ex.Message}");
        }
        Console.WriteLine();
    }
    return 0;
}

static VerifyMethod ParseMethod(string s) => s.ToLowerInvariant() switch
{
    "card" or "rfid" => VerifyMethod.Card,
    "fp" or "finger" or "digital" => VerifyMethod.Fingerprint,
    "face" => VerifyMethod.Face,
    "pin" or "teclado" => VerifyMethod.Pin,
    "nfc" => VerifyMethod.Nfc,
    "qr" => VerifyMethod.QrCode,
    "plate" or "matricula" => VerifyMethod.LicensePlate,
    _ => VerifyMethod.Face,
};

static void PrintHelp()
{
    Console.WriteLine("SIBHIK — Hikvision -> SQL Server");
    Console.WriteLine();
    Console.WriteLine("Sem argumentos: corre como serviço/aplicação (escuta os terminais).");
    Console.WriteLine();
    Console.WriteLine("Modos de teste:");
    Console.WriteLine("  --test-connection            Testa a ligação ao SQL Server e sai.");
    Console.WriteLine("  --simulate <nº> [metodo]     Insere uma picagem de teste (sem terminal).");
    Console.WriteLine("                               metodo: card|fp|face|pin|nfc|qr|plate");
    Console.WriteLine("  --sync-users                 iVMS -> SQL: cria no SQL os utilizadores dos terminais.");
    Console.WriteLine("  --export-users               SQL -> terminais: cria nos terminais os utilizadores do SQL.");
    Console.WriteLine("  --sync-validity              Sincroniza a data de fim de validade (SQL <-> terminais).");
    Console.WriteLine("  --nomes-profissionais [todos] Preenche ID_NOME_PROFISSIONAL (1º+último nome) na TG_FUNCIONARIOS.");
    Console.WriteLine("  --renumerar [id|todos]       Tira zeros à esquerda no terminal (00137 -> 137) migrando as biometrias.");
    Console.WriteLine("                               Sem argumento mostra só o plano; <id> migra um; 'todos' migra todos.");
    Console.WriteLine("  --config                     Abre a janela de configuração (no browser).");
    Console.WriteLine("  --help                       Mostra esta ajuda.");
}

// Pasta REAL onde está o SIBHIK.exe. Em modo single-file, AppContext.BaseDirectory
// pode apontar para uma pasta temporária de extracção — por isso preferimos o
// caminho do próprio executável (Environment.ProcessPath), para o config.json
// ficar sempre ao lado do SIBHIK.exe (e ser o mesmo que o serviço lê e a janela grava).
static string ExeDirectory()
{
    var proc = Environment.ProcessPath;
    if (!string.IsNullOrEmpty(proc) &&
        !Path.GetFileNameWithoutExtension(proc).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
    {
        var dir = Path.GetDirectoryName(proc);
        if (!string.IsNullOrEmpty(dir)) return dir;
    }
    return AppContext.BaseDirectory; // fallback (ex.: correr via 'dotnet App.dll')
}

static AppConfig LoadConfig(string path)
{
    if (!File.Exists(path))
    {
        // Cria um ficheiro de exemplo na primeira execução, para o cliente
        // saber o que preencher.
        var sample = new AppConfig
        {
            SqlServer = new SqlServerConfig
            {
                Server = "DESKTOP-S8CKGL7\\SQL",
                Database = "SPBA1",
                Table = "TG_MOVIMENTOS",
                User = "sa",
                Password = "",
            },
            Equipamentos =
            {
                new DeviceConfig { Name = "Porta Principal", Ip = "192.168.1.25", Port = 80, User = "admin", Password = "" },
            },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(sample, JsonOpts()));
        Console.WriteLine($"Criado ficheiro de configuração de exemplo em: {path}");
        return sample;
    }

    var json = File.ReadAllText(path);
    return JsonSerializer.Deserialize<AppConfig>(json, JsonOpts())
           ?? throw new InvalidOperationException("config.json inválido.");
}

static JsonSerializerOptions JsonOpts() => new()
{
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
};

/// <summary>Logger simples para os modos de teste por linha de comandos.</summary>
sealed class ConsoleAppLogger : IAppLogger
{
    public void Info(string message) => Console.WriteLine(message);
    public void Warn(string message) => Console.WriteLine("AVISO: " + message);
    public void Error(string message) => Console.WriteLine("ERRO: " + message);
    public void Raw(string terminal, string payload) { }
}
