namespace HikvisionSqlBridge.Core.Configuration;

/// <summary>
/// Configuração completa da aplicação, carregada de appsettings.json ou definida
/// através da janela de configuração. Nada está fixo no código — a mesma
/// ferramenta serve qualquer servidor Windows e qualquer instância SQL Server.
/// </summary>
public sealed class AppConfig
{
    public SqlServerConfig SqlServer { get; set; } = new();

    /// <summary>Lista de terminais Hikvision. Pode ter um ou vários, de modelos diferentes.</summary>
    public List<DeviceConfig> Equipamentos { get; set; } = new();

    public LoggingConfig Logging { get; set; } = new();

    /// <summary>Sincronização automática dos utilizadores (iVMS -> SQL). Fase 2.</summary>
    public UserSyncConfig UserSync { get; set; } = new();

    /// <summary>
    /// Pasta onde está o config.json (e onde se guarda o estado da sincronização
    /// de validade). Não é gravada no ficheiro — é preenchida ao carregar, para
    /// os ficheiros de estado ficarem sempre ao lado do executável/config.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string BaseDirectory { get; set; } = AppContext.BaseDirectory;
}

/// <summary>
/// Configuração da sincronização de utilizadores (Fase 2). Quando ligada, o
/// serviço lê periodicamente os utilizadores inscritos nos terminais e cria/
/// atualiza a ficha em TG_FUNCIONARIOS e os identificadores em TA_IDENTIFICADORES.
/// </summary>
public sealed class UserSyncConfig
{
    /// <summary>Ligar/desligar a sincronização de utilizadores.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Sentido da sincronização, conforme o cenário:
    /// "ivms-to-sql" = do terminal para o SQL (cria em TG_FUNCIONARIOS/TA_IDENTIFICADORES); (por omissão)
    /// "sql-to-ivms" = do SQL para os terminais (cria os utilizadores nos equipamentos);
    /// "both"        = os dois sentidos.
    /// Em qualquer caso só cria o que não existe — nunca altera dados já lá.
    /// </summary>
    public string Direction { get; set; } = "ivms-to-sql";

    /// <summary>Intervalo (minutos) entre sincronizações.</summary>
    public int IntervalMinutes { get; set; } = 5;

    public string FuncionariosTable { get; set; } = "TG_FUNCIONARIOS";
    public string IdentificadoresTable { get; set; } = "TA_IDENTIFICADORES";

    /// <summary>Anos de validade por omissão (fim = início + este valor), quando o terminal não indica.</summary>
    public int ValidityYears { get; set; } = 10;

    /// <summary>
    /// Sincronizar ALTERAÇÕES da data de fim de validade. Ao contrário da criação
    /// (que só cria o que falta), isto acompanha mudanças: se alterar a validade,
    /// o(s) terminal(is) seguem. Assim, para dar saída a um funcionário basta
    /// mexer num sítio.
    /// </summary>
    public bool SyncValidity { get; set; } = false;

    /// <summary>
    /// Quem manda na data de validade quando os dois lados diferem:
    /// "sql"  = o SQL manda sempre; os terminais seguem o SQL (regra fixa, previsível); (por omissão)
    /// "both" = nos dois sentidos — o lado que foi alterado é que manda (com desempate).
    /// </summary>
    public string ValidityMaster { get; set; } = "sql";

    public bool ValiditySqlIsMaster => !string.Equals(ValidityMaster, "both", StringComparison.OrdinalIgnoreCase);

    public bool DoImportToSql => Direction is "ivms-to-sql" or "both";
    public bool DoExportToTerminal => Direction is "sql-to-ivms" or "both";
}

/// <summary>
/// Ligação ao SQL Server. O campo <see cref="Server"/> aceita as várias formas
/// habituais: "NOMEPC\\SQLEXPRESS", "192.168.2.1\\SQLEXPRESS", "srvcmasql3" ou
/// "192.168.2.1,1433". Suporta autenticação SQL (utilizador/palavra-passe) ou
/// autenticação integrada do Windows.
/// </summary>
public sealed class SqlServerConfig
{
    /// <summary>
    /// Opcional. Se for preenchido, é usado tal e qual (permite colar
    /// directamente uma connection string completa, ex.: a que o bevotech usa:
    /// "Data Source=DESKTOP-S8CKGL7\\SQL;Initial Catalog=SPBA1;User Id=sa;Password=...;
    /// MultipleActiveResultSets=True"). Se ficar vazio, a ligação é montada a
    /// partir dos campos abaixo.
    /// </summary>
    public string? ConnectionString { get; set; }

    public string Server { get; set; } = "";
    public string Database { get; set; } = "Assiduidadev3";
    public string Table { get; set; } = "TG_MOVIMENTOS";

    public bool UseWindowsAuth { get; set; }
    public string User { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>
    /// Encriptação TLS da ligação. Por omissão FALSE, tal como o software
    /// bevotech (System.Data.SqlClient) — as instâncias locais/internas
    /// costumam não ter certificado TLS válido, e o driver novo
    /// (Microsoft.Data.SqlClient) rejeita a ligação se exigir encriptação.
    /// </summary>
    public bool Encrypt { get; set; } = false;

    /// <summary>Confia no certificado do servidor (útil em instâncias locais sem certificado válido).</summary>
    public bool TrustServerCertificate { get; set; } = true;

    /// <summary>Segundos de espera na ligação ao SQL Server.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 15;

    public string BuildConnectionString()
    {
        Microsoft.Data.SqlClient.SqlConnectionStringBuilder b;

        if (!string.IsNullOrWhiteSpace(ConnectionString))
        {
            // Connection string colada pelo cliente: partimos dela...
            b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(ConnectionString);
        }
        else
        {
            b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
            {
                DataSource = Server,
                InitialCatalog = Database,
                ConnectTimeout = ConnectTimeoutSeconds,
                MultipleActiveResultSets = true,
            };

            if (UseWindowsAuth)
                b.IntegratedSecurity = true;
            else
            {
                b.UserID = User;
                b.Password = Password;
            }
        }

        // ...e garantimos sempre estas duas, que são a causa mais comum de
        // falha de login contra instâncias locais sem certificado TLS.
        b.Encrypt = Encrypt;
        b.TrustServerCertificate = TrustServerCertificate;

        return b.ConnectionString;
    }
}

/// <summary>
/// Um terminal Hikvision. O serviço liga-se a cada equipamento pelo canal de
/// eventos ISAPI (long-polling) e recebe cada picagem assim que ocorre.
/// </summary>
public sealed class DeviceConfig
{
    /// <summary>Nome amigável, apenas para os logs (ex.: "Porta Principal").</summary>
    public string Name { get; set; } = "";

    public string Ip { get; set; } = "";
    public int Port { get; set; } = 80;
    public bool UseHttps { get; set; }

    public string User { get; set; } = "admin";
    public string Password { get; set; } = "";

    /// <summary>
    /// Como o serviço obtém as picagens do terminal:
    /// "poll"   = consulta periódica à API AcsEvent (como a própria página web do
    ///            terminal) — fiável e funciona em qualquer produto Hikvision,
    ///            incluindo os terminais faciais que não empurram eventos. (por omissão)
    /// "stream" = escuta o canal ISAPI alertStream (long-polling) — mais imediato,
    ///            mas nem todos os modelos enviam eventos por aqui.
    /// </summary>
    public string Mode { get; set; } = "poll";

    /// <summary>Intervalo (segundos) entre consultas no modo "poll".</summary>
    public int PollIntervalSeconds { get; set; } = 3;

    /// <summary>Timeout (segundos) dos pedidos HTTP no modo "poll".</summary>
    public int HttpTimeoutSeconds { get; set; } = 15;

    public bool IsPollMode => !string.Equals(Mode, "stream", StringComparison.OrdinalIgnoreCase);

    /// <summary>Base do URL do equipamento (http://ip:porta), calculada a partir dos campos acima.</summary>
    public string BaseUrl => $"{(UseHttps ? "https" : "http")}://{Ip}:{Port}";

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Ip : $"{Name} ({Ip})";
}

public sealed class LoggingConfig
{
    /// <summary>Pasta onde ficam os ficheiros de log rotativos para auditoria.</summary>
    public string Directory { get; set; } = "logs";

    /// <summary>Dias de retenção dos logs.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Também escrever os eventos brutos recebidos do terminal (diagnóstico).</summary>
    public bool LogRawEvents { get; set; } = true;
}
