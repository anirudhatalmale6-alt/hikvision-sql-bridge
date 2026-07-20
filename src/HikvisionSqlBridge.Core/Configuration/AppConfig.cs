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
