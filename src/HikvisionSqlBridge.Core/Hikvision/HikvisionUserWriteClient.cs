using System.Net;
using System.Text;
using System.Text.Json;
using HikvisionSqlBridge.Core.Configuration;
using HikvisionSqlBridge.Core.Diagnostics;

namespace HikvisionSqlBridge.Core.Hikvision;

/// <summary>
/// Cria utilizadores NO terminal (sentido SQL -> iVMS) pela API ISAPI
/// (POST /ISAPI/AccessControl/UserInfo/Record?format=json). Cria a ficha do
/// utilizador (número, nome, validade e permissão de porta). A impressão
/// digital / face não se envia por aqui — é inscrita no próprio terminal.
/// </summary>
public sealed class HikvisionUserWriteClient : IDisposable
{
    private const string RecordPath = "/ISAPI/AccessControl/UserInfo/Record?format=json";
    private const string ModifyPath = "/ISAPI/AccessControl/UserInfo/Modify?format=json";

    private readonly DeviceConfig _device;
    private readonly IAppLogger _log;
    private readonly HttpClient _http;

    public HikvisionUserWriteClient(DeviceConfig device, IAppLogger log)
    {
        _device = device;
        _log = log;

        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(device.User, device.Password),
            PreAuthenticate = true,
        };
        if (device.UseHttps)
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, device.HttpTimeoutSeconds)),
        };
    }

    /// <summary>
    /// Cria um utilizador no terminal. Devolve true se o terminal aceitou.
    /// </summary>
    public async Task<bool> CreateUserAsync(string employeeNo, string name, DateTime begin, DateTime end, CancellationToken ct)
    {
        var url = _device.BaseUrl + RecordPath;
        var body = BuildRequest(employeeNo, name, begin, end);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        _log.Raw(_device.DisplayName, json);

        if (!resp.IsSuccessStatusCode)
        {
            _log.Warn($"Terminal {_device.DisplayName}: falha a criar utilizador {employeeNo} (HTTP {(int)resp.StatusCode}).");
            return false;
        }

        // O terminal responde com statusCode/statusString; 1 = OK.
        return ResponseIsOk(json);
    }

    /// <summary>
    /// Altera um utilizador já existente no terminal (usado para atualizar a data
    /// de fim de validade). Mantém o nome e a data de início; muda a validade e
    /// reenvia a permissão de porta para não a perder. Devolve true se o terminal
    /// aceitou (ou não havia nada para mudar).
    /// </summary>
    public async Task<bool> ModifyUserAsync(string employeeNo, string name, DateTime begin, DateTime end, CancellationToken ct)
    {
        var url = _device.BaseUrl + ModifyPath;
        var body = BuildRequest(employeeNo, name, begin, end);

        using var req = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        _log.Raw(_device.DisplayName, json);

        if (!resp.IsSuccessStatusCode)
        {
            _log.Warn($"Terminal {_device.DisplayName}: falha a alterar utilizador {employeeNo} (HTTP {(int)resp.StatusCode}).");
            return false;
        }

        return ResponseIsOk(json);
    }

    internal static string BuildRequest(string employeeNo, string name, DateTime begin, DateTime end)
    {
        // Inclui permissão de porta (RightPlan) para o utilizador poder picar.
        var nameEscaped = JsonEncodedText.Encode(name ?? "").ToString();
        return
            "{\"UserInfo\":{" +
            $"\"employeeNo\":\"{employeeNo}\"," +
            "\"userType\":\"normal\"," +
            $"\"name\":\"{nameEscaped}\"," +
            "\"Valid\":{\"enable\":true," +
            $"\"beginTime\":\"{begin:yyyy-MM-ddTHH:mm:ss}\"," +
            $"\"endTime\":\"{end:yyyy-MM-ddTHH:mm:ss}\"," +
            "\"timeType\":\"local\"}," +
            "\"doorRight\":\"1\"," +
            "\"RightPlan\":[{\"doorNo\":1,\"planTemplateNo\":\"1\"}]" +
            "}}";
    }

    internal static bool ResponseIsOk(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("statusCode", out var sc) && sc.TryGetInt32(out var code))
                return code == 1;
            if (root.TryGetProperty("statusString", out var ss))
                return string.Equals(ss.GetString(), "OK", StringComparison.OrdinalIgnoreCase);
        }
        catch { /* resposta não-JSON */ }
        return false;
    }

    public void Dispose() => _http.Dispose();
}
