using System.Net;
using System.Text;
using System.Text.Json;
using HikvisionSqlBridge.Core.Configuration;
using HikvisionSqlBridge.Core.Diagnostics;
using HikvisionSqlBridge.Core.Model;

namespace HikvisionSqlBridge.Core.Hikvision;

/// <summary>
/// Lê os utilizadores inscritos no terminal pela API UserInfo do ISAPI
/// (POST /ISAPI/AccessControl/UserInfo/Search?format=json). É o que permite
/// sincronizar automaticamente os utilizadores do iVMS para o SQL, sem os
/// inserir à mão nas duas aplicações.
/// </summary>
public sealed class HikvisionUserInfoClient : IDisposable
{
    private const string UserInfoSearchPath = "/ISAPI/AccessControl/UserInfo/Search?format=json";

    private readonly DeviceConfig _device;
    private readonly IAppLogger _log;
    private readonly HttpClient _http;

    public HikvisionUserInfoClient(DeviceConfig device, IAppLogger log)
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

    /// <summary>Lê todos os utilizadores inscritos no terminal (trata da paginação).</summary>
    public async Task<List<TerminalUser>> GetAllUsersAsync(CancellationToken ct)
    {
        var url = _device.BaseUrl + UserInfoSearchPath;
        var users = new List<TerminalUser>();
        int position = 0;
        const int maxResults = 30;

        while (!ct.IsCancellationRequested)
        {
            var body =
                "{\"UserInfoSearchCond\":{" +
                "\"searchID\":\"SIBHIK\"," +
                $"\"searchResultPosition\":{position}," +
                $"\"maxResults\":{maxResults}" +
                "}}";

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            _log.Raw(_device.DisplayName, json);

            var (batch, status, count) = ParseResponse(json);
            users.AddRange(batch);

            if (count == 0 || !string.Equals(status, "MORE", StringComparison.OrdinalIgnoreCase))
                break;
            position += count;
        }

        return users;
    }

    /// <summary>Interpreta a resposta JSON do UserInfo/Search.</summary>
    internal static (List<TerminalUser> users, string status, int count) ParseResponse(string json)
    {
        var list = new List<TerminalUser>();
        string status = "OK";
        int count = 0;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("UserInfoSearch", out var search))
            return (list, status, count);

        if (search.TryGetProperty("responseStatusStrg", out var st) && st.ValueKind == JsonValueKind.String)
            status = st.GetString() ?? "OK";

        // A lista pode chamar-se "UserInfo" ou "UserInfoList" conforme o firmware.
        if (!search.TryGetProperty("UserInfo", out var arr) || arr.ValueKind != JsonValueKind.Array)
            search.TryGetProperty("UserInfoList", out arr);

        if (arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                count++;
                var u = ParseUser(item);
                if (u is not null)
                    list.Add(u);
            }
        }

        return (list, status, count);
    }

    private static TerminalUser? ParseUser(JsonElement item)
    {
        var employeeNo = GetString(item, "employeeNo") ?? GetString(item, "employeeNoString");
        if (string.IsNullOrWhiteSpace(employeeNo))
            return null;

        var user = new TerminalUser
        {
            EmployeeNo = employeeNo.Trim(),
            Name = GetString(item, "name") ?? "",
        };

        // Validade (bloco "Valid": { beginTime, endTime }).
        if (item.TryGetProperty("Valid", out var valid) && valid.ValueKind == JsonValueKind.Object)
        {
            if (DateTimeOffset.TryParse(GetString(valid, "beginTime"), out var b)) user.ValidBegin = b.DateTime;
            if (DateTimeOffset.TryParse(GetString(valid, "endTime"), out var e)) user.ValidEnd = e.DateTime;
        }

        // Métodos: pelos contadores, quando o firmware os fornece.
        int cards = GetInt(item, "numOfCard") ?? 0;
        int fps = (GetInt(item, "numOfFP") ?? GetInt(item, "numOfFinger") ?? 0);
        int faces = GetInt(item, "numOfFace") ?? 0;
        int pins = (GetInt(item, "numOfPassword") ?? GetInt(item, "numOfPIN") ?? 0);
        bool hasAnyCount = item.TryGetProperty("numOfCard", out _) ||
                           item.TryGetProperty("numOfFP", out _) ||
                           item.TryGetProperty("numOfFinger", out _) ||
                           item.TryGetProperty("numOfFace", out _);

        user.HasCard = cards > 0;
        user.HasPin = pins > 0;
        // Se o firmware não der contadores, assumimos digital/face (tipo 2), que é
        // o método mais comum nestes terminais — afinado com o equipamento real.
        user.HasFingerprintOrFace = hasAnyCount ? (fps > 0 || faces > 0) : true;

        return user;
    }

    private static string? GetString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            _ => null,
        };
    }

    private static int? GetInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    public void Dispose() => _http.Dispose();
}
