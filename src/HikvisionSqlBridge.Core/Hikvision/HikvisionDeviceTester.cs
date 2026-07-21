using System.Net;
using HikvisionSqlBridge.Core.Configuration;
using HikvisionSqlBridge.Core.Diagnostics;

namespace HikvisionSqlBridge.Core.Hikvision;

/// <summary>
/// Teste rápido de ligação a um terminal. Usa exactamente a mesma consulta que
/// o serviço usa para ler as picagens (AcsEvent) — assim, se o teste passar, é
/// garantido que o serviço também consegue ler as picagens deste terminal com
/// estas credenciais. (Não usamos /System/deviceInfo porque há contas que leem
/// os eventos mas não têm permissão nesse endpoint, dando 401 enganador.)
/// </summary>
public static class HikvisionDeviceTester
{
    public static async Task<(bool ok, string message)> TestAsync(DeviceConfig device, CancellationToken ct = default)
    {
        try
        {
            using var client = new HikvisionAcsEventClient(device, NullLogger.Instance);
            var end = DateTimeOffset.Now;
            var start = end.AddMinutes(-2);
            var events = await client.QueryAsync(start, end, ct);
            return (true, $"Ligação ao terminal OK (respondeu à consulta de picagens; {events.Count} nos últimos 2 min).");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            return (false, "Credenciais recusadas (401). Verifique o utilizador e a password.");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Não foi possível ligar: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return (false, "Sem resposta (timeout). Verifique o IP, a porta e a rede.");
        }
        catch (Exception ex)
        {
            return (false, "Falha no teste ao terminal: " + ex.Message);
        }
    }

    private sealed class NullLogger : IAppLogger
    {
        public static readonly NullLogger Instance = new();
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Raw(string terminal, string payload) { }
    }
}
