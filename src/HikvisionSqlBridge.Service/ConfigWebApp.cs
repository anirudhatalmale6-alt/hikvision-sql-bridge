using System.Diagnostics;
using HikvisionSqlBridge.Core.Configuration;
using HikvisionSqlBridge.Core.Data;
using HikvisionSqlBridge.Core.Diagnostics;
using HikvisionSqlBridge.Core.Hikvision;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HikvisionSqlBridge.Service;

/// <summary>
/// Janela de configuração gráfica. Em vez de uma janela nativa do Windows,
/// levanta um pequeno servidor local (só acessível na própria máquina) e abre
/// o browser numa página com o formulário de configuração. Assim configura-se
/// o SQL Server e os terminais sem editar ficheiros à mão, com botões de
/// "Testar ligação". Ao gravar, escreve o mesmo config.json que o serviço usa.
/// </summary>
public static class ConfigWebApp
{
    public static async Task<int> RunAsync(string configPath)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions());
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); // porta livre escolhida pelo sistema

        var app = builder.Build();

        // Página principal (formulário).
        app.MapGet("/", async ctx =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(ConfigPage.Html);
        });

        // Ícone da aplicação, como favicon da janela.
        app.MapGet("/favicon.ico", () =>
        {
            var bytes = LoadIcon();
            return bytes is null ? Results.NotFound() : Results.File(bytes, "image/x-icon");
        });

        // Devolve a configuração actual (para preencher o formulário).
        app.MapGet("/api/config", () =>
            Results.Json(ConfigStore.LoadOrNew(configPath), ConfigStore.Options));

        // Grava a configuração recebida do formulário.
        app.MapPost("/api/save", (AppConfig cfg) =>
        {
            try
            {
                ConfigStore.Save(configPath, cfg ?? new AppConfig());
                return Results.Json(new { ok = true, message = $"Configuração gravada em {configPath}" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, message = "Erro a gravar: " + ex.Message });
            }
        });

        // Testa a ligação ao SQL Server com os dados do formulário.
        app.MapPost("/api/test-sql", async (SqlServerConfig sql) =>
        {
            sql ??= new SqlServerConfig();
            var log = new CapturingLogger();
            var repo = new MovimentoRepository(sql, log);
            var ok = await repo.TestConnectionAsync();
            var message = ok
                ? $"Ligação ao SQL Server OK ({sql.Database})."
                : (log.LastError ?? "Falha na ligação ao SQL Server.");
            return Results.Json(new { ok, message });
        });

        // Testa a ligação a um terminal com os dados do formulário.
        app.MapPost("/api/test-terminal", async (DeviceConfig dev) =>
        {
            var (ok, message) = await HikvisionDeviceTester.TestAsync(dev ?? new DeviceConfig());
            return Results.Json(new { ok, message });
        });

        // Fecha a janela de configuração (encerra o servidor local).
        app.MapPost("/api/close", (Microsoft.Extensions.Hosting.IHostApplicationLifetime life) =>
        {
            life.StopApplication();
            return Results.Json(new { ok = true });
        });

        await app.StartAsync();

        // Descobre a porta que o sistema atribuiu e abre o browser lá.
        var url = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? "http://127.0.0.1:5000";

        Console.WriteLine($"Janela de configuração SIBHIK aberta em {url}");
        Console.WriteLine("(Se o browser não abrir sozinho, copie o endereço acima para o browser.)");
        TryOpenBrowser(url);

        await app.WaitForShutdownAsync();
        return 0;
    }

    private static byte[]? LoadIcon()
    {
        var asm = typeof(ConfigWebApp).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("SIBHIK.ico", StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;
        using var s = asm.GetManifestResourceStream(name);
        if (s is null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // Em alguns ambientes UseShellExecute falha; tenta o explorer (Windows).
            try { Process.Start("explorer", url); } catch { /* mostra-se o URL na consola */ }
        }
    }

    /// <summary>Logger que guarda o último erro, para o devolver ao formulário.</summary>
    private sealed class CapturingLogger : IAppLogger
    {
        public string? LastError { get; private set; }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) => LastError = message;
        public void Raw(string terminal, string payload) { }
    }
}
