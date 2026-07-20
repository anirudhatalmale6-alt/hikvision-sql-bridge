using System.Text.Json;
using HikvisionSqlBridge.Core.Configuration;
using HikvisionSqlBridge.Core.Data;
using HikvisionSqlBridge.Core.Diagnostics;
using HikvisionSqlBridge.Core.Mapping;
using HikvisionSqlBridge.Core.Model;
using HikvisionSqlBridge.Service;

// Carrega a configuração a partir de config.json (ao lado do executável).
// Este é o mesmo ficheiro que a janela de configuração grava, por isso a
// ferramenta funciona em qualquer servidor só editando/gerando este ficheiro.
var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
var appConfig = LoadConfig(configPath);

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
    Console.WriteLine("  --help                       Mostra esta ajuda.");
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
