using System.Text.Json;
using HikvisionSqlBridge.Core.Configuration;
using HikvisionSqlBridge.Core.Diagnostics;
using HikvisionSqlBridge.Service;

// Carrega a configuração a partir de config.json (ao lado do executável).
// Este é o mesmo ficheiro que a janela de configuração grava, por isso a
// ferramenta funciona em qualquer servidor só editando/gerando este ficheiro.
var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
var appConfig = LoadConfig(configPath);

var builder = Host.CreateApplicationBuilder(args);

// Permite instalar/correr como Serviço do Windows.
builder.Services.AddWindowsService(o => o.ServiceName = "HikvisionSqlBridge");

IAppLogger logger = new FileAppLogger(appConfig.Logging);
builder.Services.AddSingleton(appConfig);
builder.Services.AddSingleton(logger);
builder.Services.AddHostedService<BridgeWorker>();

var host = builder.Build();
host.Run();

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
                Server = "srvcmasql3",
                Database = "Assiduidadev3",
                Table = "TG_MOVIMENTOS",
                User = "bevotech",
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
