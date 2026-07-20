using System.Net;
using System.Text;
using System.Text.Json;
using HikvisionSqlBridge.Core.Configuration;
using HikvisionSqlBridge.Core.Diagnostics;
using HikvisionSqlBridge.Core.Model;

namespace HikvisionSqlBridge.Core.Hikvision;

/// <summary>
/// Lê as picagens do terminal pela API AcsEvent do ISAPI
/// (POST /ISAPI/AccessControl/AcsEvent?format=json) — exactamente o mesmo
/// mecanismo que a página web do terminal usa na lista "Pesquisa de evento".
/// Funciona em qualquer produto Hikvision, incluindo os terminais faciais que
/// não empurram eventos pelo alertStream.
/// </summary>
public sealed class HikvisionAcsEventClient : IDisposable
{
    private const string AcsEventPath = "/ISAPI/AccessControl/AcsEvent?format=json";

    private readonly DeviceConfig _device;
    private readonly IAppLogger _log;
    private readonly HttpClient _http;

    public HikvisionAcsEventClient(DeviceConfig device, IAppLogger log)
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
    /// Consulta as picagens entre <paramref name="start"/> e <paramref name="end"/>.
    /// Trata da paginação (responseStatusStrg = "MORE") automaticamente.
    /// </summary>
    public async Task<List<AccessEvent>> QueryAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var url = _device.BaseUrl + AcsEventPath;
        var events = new List<AccessEvent>();
        int position = 0;
        const int maxResults = 50;

        while (!ct.IsCancellationRequested)
        {
            var body = BuildRequest(start, end, position, maxResults);

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            _log.Raw(_device.DisplayName, json);

            var (batch, status, count) = ParseResponse(json, _device.Ip);
            events.AddRange(batch);

            // Continua a paginar enquanto o terminal disser que há mais.
            if (count == 0 || !string.Equals(status, "MORE", StringComparison.OrdinalIgnoreCase))
                break;
            position += count;
        }

        return events;
    }

    private static string BuildRequest(DateTimeOffset start, DateTimeOffset end, int position, int maxResults)
    {
        // JSON construído à mão para controlar o formato de data exactamente como
        // o Hikvision espera (ISO 8601 com fuso horário, ex.: 2026-07-20T15:40:00+01:00).
        return "{\"AcsEventCond\":{" +
               "\"searchID\":\"SIBHIK\"," +
               $"\"searchResultPosition\":{position}," +
               $"\"maxResults\":{maxResults}," +
               "\"major\":5,\"minor\":0," +
               $"\"startTime\":\"{start:yyyy-MM-ddTHH:mm:sszzz}\"," +
               $"\"endTime\":\"{end:yyyy-MM-ddTHH:mm:sszzz}\"" +
               "}}";
    }

    /// <summary>
    /// Interpreta a resposta JSON do AcsEvent. Devolve as picagens válidas (com
    /// funcionário), o estado (OK/MORE/NoMoreData) e o número de registos lidos.
    /// </summary>
    internal static (List<AccessEvent> events, string status, int count) ParseResponse(string json, string terminalIp)
    {
        var list = new List<AccessEvent>();
        string status = "OK";
        int count = 0;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("AcsEvent", out var acs))
            return (list, status, count);

        if (acs.TryGetProperty("responseStatusStrg", out var st) && st.ValueKind == JsonValueKind.String)
            status = st.GetString() ?? "OK";

        if (acs.TryGetProperty("InfoList", out var info) && info.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in info.EnumerateArray())
            {
                count++;
                var ev = ParseEvent(item, terminalIp);
                if (ev is not null)
                    list.Add(ev);
            }
        }

        return (list, status, count);
    }

    private static AccessEvent? ParseEvent(JsonElement item, string terminalIp)
    {
        // Sem funcionário => é um evento de porta / alarme, não é picagem.
        var employee = GetString(item, "employeeNoString") ?? GetString(item, "employeeNo");
        if (string.IsNullOrWhiteSpace(employee) || employee == "0")
            return null;

        var time = DateTime.Now;
        var timeStr = GetString(item, "time");
        if (DateTimeOffset.TryParse(timeStr, out var dto))
            // Usamos a hora tal como o terminal a escreve (sem conversão de fuso).
            // Alguns terminais marcam o evento com fuso +00:00 mas a hora já é a
            // local que o terminal mostra; converter para "local" adicionava 1h.
            time = dto.DateTime;

        // Método: preferir currentVerifyMode; se não vier, tentar pelo cartão.
        var verifyMode = GetString(item, "currentVerifyMode");
        var method = VerifyModeParser.Parse(verifyMode);
        if (method == VerifyMethod.Unknown)
        {
            var cardNo = GetString(item, "cardNo");
            if (!string.IsNullOrWhiteSpace(cardNo) && cardNo != "0")
                method = VerifyMethod.Card;
        }

        var serial = GetLong(item, "serialNo");

        return new AccessEvent
        {
            EventTime = time,
            EmployeeNo = employee.Trim(),
            Method = method,
            TerminalIp = terminalIp,
            Granted = true,
            SerialNo = serial?.ToString(),
        };
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

    private static long? GetLong(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    public void Dispose() => _http.Dispose();
}
