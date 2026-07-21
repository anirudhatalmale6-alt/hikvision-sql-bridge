using System.Net;
using HikvisionSqlBridge.Core.Configuration;

namespace HikvisionSqlBridge.Core.Hikvision;

/// <summary>
/// Teste rápido de ligação a um terminal: valida o IP/porta e as credenciais
/// (autenticação digest) através do endpoint ISAPI /System/deviceInfo, o mesmo
/// que devolve o modelo e o nº de série do equipamento. Usado pelo botão
/// "Testar terminal" da janela de configuração.
/// </summary>
public static class HikvisionDeviceTester
{
    public static async Task<(bool ok, string message)> TestAsync(DeviceConfig device, CancellationToken ct = default)
    {
        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(device.User, device.Password),
            PreAuthenticate = true,
        };
        if (device.UseHttps)
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, device.HttpTimeoutSeconds)),
        };

        var url = device.BaseUrl + "/ISAPI/System/deviceInfo";
        try
        {
            using var resp = await http.GetAsync(url, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                return (false, "Credenciais recusadas (401). Verifique o utilizador e a password.");

            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct);

            var model = Between(body, "<model>", "</model>");
            var info = string.IsNullOrWhiteSpace(model) ? "" : $" — {model}";
            return (true, $"Ligação ao terminal OK{info}.");
        }
        catch (TaskCanceledException)
        {
            return (false, "Sem resposta (timeout). Verifique o IP, a porta e a rede.");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Não foi possível ligar: {ex.Message}");
        }
    }

    private static string Between(string s, string a, string b)
    {
        var i = s.IndexOf(a, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        i += a.Length;
        var j = s.IndexOf(b, i, StringComparison.OrdinalIgnoreCase);
        return j < 0 ? "" : s[i..j].Trim();
    }
}
