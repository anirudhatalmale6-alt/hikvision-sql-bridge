using HikvisionSqlBridge.Core.Configuration;
using Xunit;

namespace HikvisionSqlBridge.Tests;

public class SqlServerConfigTests
{
    [Fact]
    public void BuildConnectionString_keeps_pasted_string_and_forces_encrypt_false()
    {
        var raw = "Data Source=DESKTOP-S8CKGL7\\SQL;Initial Catalog=SPBA1;User Id=sa;Password=xxx;MultipleActiveResultSets=True;";
        var cfg = new SqlServerConfig { ConnectionString = raw };

        var cs = cfg.BuildConnectionString();

        // Mantém o que veio na string colada...
        Assert.Contains("Data Source=DESKTOP-S8CKGL7\\SQL", cs);
        Assert.Contains("Initial Catalog=SPBA1", cs);
        Assert.Contains("User ID=sa", cs);
        Assert.Contains("Multiple Active Result Sets=True", cs);
        // ...e força Encrypt=False + TrustServerCertificate=True (correção do login).
        Assert.Contains("Encrypt=False", cs);
        Assert.Contains("Trust Server Certificate=True", cs);
    }

    [Fact]
    public void BuildConnectionString_builds_from_parts_with_sql_auth()
    {
        var cfg = new SqlServerConfig
        {
            Server = "DESKTOP-S8CKGL7\\SQL",
            Database = "SPBA1",
            User = "sa",
            Password = "secret",
        };

        var cs = cfg.BuildConnectionString();

        Assert.Contains("Data Source=DESKTOP-S8CKGL7\\SQL", cs);
        Assert.Contains("Initial Catalog=SPBA1", cs);
        Assert.Contains("User ID=sa", cs);
        Assert.Contains("Multiple Active Result Sets=True", cs);
        Assert.Contains("Encrypt=False", cs);
        Assert.DoesNotContain("Integrated Security", cs);
    }

    [Fact]
    public void BuildConnectionString_builds_from_parts_with_windows_auth()
    {
        var cfg = new SqlServerConfig
        {
            Server = "srvcmasql3\\SQLEXPRESS",
            Database = "Assiduidadev3",
            UseWindowsAuth = true,
        };

        var cs = cfg.BuildConnectionString();

        Assert.Contains("Integrated Security=True", cs);
        Assert.DoesNotContain("User ID=", cs);
    }
}
