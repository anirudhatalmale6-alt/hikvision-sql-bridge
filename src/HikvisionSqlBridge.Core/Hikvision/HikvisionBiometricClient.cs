using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HikvisionSqlBridge.Core.Configuration;
using HikvisionSqlBridge.Core.Diagnostics;

namespace HikvisionSqlBridge.Core.Hikvision;

/// <summary>
/// Lê e volta a gravar as biometrias de um utilizador no terminal (impressões
/// digitais, cartões e face), pela API ISAPI. Serve para MIGRAR o que está
/// inscrito num número com zeros à esquerda (ex.: 00137) para o número sem
/// zeros (137), sem a pessoa ter de voltar a picar.
///
/// Testado contra o modelo DS-K1T342MFWX-E1 (MinMoe, firmware V4.38): a face
/// fica na biblioteca facial (FDLib) e é recuperável pelo faceURL; as digitais
/// vêm em base64 pelo FingerPrintDownload; os cartões pelo CardInfo/Search.
/// </summary>
public sealed class HikvisionBiometricClient : IDisposable
{
    private readonly DeviceConfig _device;
    private readonly IAppLogger _log;
    private readonly HttpClient _http;

    public HikvisionBiometricClient(DeviceConfig device, IAppLogger log)
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
            // A migração de biometrias é mais pesada que um pedido normal.
            Timeout = TimeSpan.FromSeconds(Math.Max(30, device.HttpTimeoutSeconds)),
        };
    }

    // ------------------------------------------------------------------
    // Impressões digitais
    // ------------------------------------------------------------------

    public sealed record FingerTemplate(string FingerData, int FingerPrintId, string FingerType, IReadOnlyList<int> EnableCardReader);

    /// <summary>Lê as impressões digitais gravadas num número de utilizador.</summary>
    public async Task<List<FingerTemplate>> DownloadFingerprintsAsync(string employeeNo, CancellationToken ct)
    {
        // Este firmware (V4.38) exige o registo completo no pedido: employeeNo,
        // enableCardReader, fingerPrintID E fingerType. Não é uma "lista" — é uma
        // consulta por dedo. Por isso vamos dedo a dedo (1..10) e recolhemos os que
        // tiverem digital gravada. O nó pode ser "FingerPrintCfg" (este modelo) ou
        // "FingerPrintCond" (outros firmwares).
        foreach (var node in new[] { "FingerPrintCfg", "FingerPrintCond" })
        {
            var collected = new List<FingerTemplate>();
            var seen = new HashSet<int>();
            for (int fid = 1; fid <= 10; fid++)
            {
                var one = ParseFingerprints(
                    await PostAsync(FingerPrintDownloadPath, FingerCondBody(node, employeeNo, fid), ct) ?? "",
                    employeeNo);
                foreach (var f in one)
                    if (seen.Add(f.FingerPrintId))
                        collected.Add(f);
            }
            if (collected.Count > 0)
                return collected;
        }
        return new List<FingerTemplate>();
    }

    private const string FingerPrintDownloadPath = "/ISAPI/AccessControl/FingerPrintDownload?format=json";

    private static string FingerCondBody(string node, string employeeNo, int fingerPrintId)
    {
        return
            "{\"" + node + "\":{" +
            "\"searchID\":\"SIBHIK\"," +
            $"\"employeeNo\":\"{employeeNo}\"," +
            "\"enableCardReader\":[1]," +
            "\"cardReaderNo\":1," +
            $"\"fingerPrintID\":{fingerPrintId}," +
            "\"fingerType\":\"normalFP\"" +
            "}}";
    }

    private List<FingerTemplate> ParseFingerprints(string json, string employeeNo)
    {
        var list = new List<FingerTemplate>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var obj in FindObjectsWith(doc.RootElement, "fingerData"))
            {
                var data = GetString(obj, "fingerData");
                if (string.IsNullOrWhiteSpace(data)) continue;
                var id = GetInt(obj, "fingerPrintID") ?? (list.Count + 1);
                var type = GetString(obj, "fingerType") ?? "normalFP";
                var readers = GetIntArray(obj, "enableCardReader");
                list.Add(new FingerTemplate(data!, id, type, readers.Count > 0 ? readers : new List<int> { 1 }));
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"{_device.DisplayName}: nao consegui ler as digitais de {employeeNo}: {ex.Message}");
        }
        return list;
    }

    /// <summary>
    /// Diagnóstico: pergunta ao terminal como e' que ele deixa LER as impressões
    /// digitais (capabilities dos vários endpoints) e tenta a leitura por dois
    /// caminhos. Regista as respostas completas para percebermos, sem adivinhar,
    /// qual e' o formato certo neste firmware — ou se ele simplesmente nao deixa
    /// exportar digitais (comum nos firmwares recentes, por privacidade).
    /// </summary>
    public async Task DiagnoseFingerprintAsync(string employeeNo, CancellationToken ct)
    {
        string[] capUrls =
        {
            "/ISAPI/AccessControl/FingerPrintDownload/capabilities?format=json",
            "/ISAPI/AccessControl/FingerPrintUpload/capabilities?format=json",
            "/ISAPI/AccessControl/CaptureFingerPrint/capabilities?format=json",
            "/ISAPI/AccessControl/FingerPrintCfg/capabilities?format=json",
        };
        foreach (var url in capUrls)
        {
            var json = await GetAsync(url, ct);
            _log.Info($"=== GET {url} ===");
            _log.Info(json ?? "(sem resposta / endpoint nao suportado)");
        }

        // Tentativa de LEITURA por FingerPrintUpload (nalguns firmwares e' este que
        // devolve a digital gravada, ao contrario do "Download").
        var readBody =
            "{\"FingerPrintCond\":{\"searchID\":\"SIBHIK\"," +
            $"\"employeeNo\":\"{employeeNo}\",\"enableCardReader\":[1],\"cardReaderNo\":1}}}}";
        var up = await PostAsync("/ISAPI/AccessControl/FingerPrintUpload?format=json", readBody, ct);
        _log.Info("=== POST FingerPrintUpload (tentativa de leitura) ===");
        _log.Info(up ?? "(sem resposta)");
    }

    /// <summary>Grava uma impressão digital num número de utilizador.</summary>
    public async Task<bool> UploadFingerprintAsync(string employeeNo, FingerTemplate fp, CancellationToken ct)
    {
        var readers = string.Join(",", fp.EnableCardReader);
        string Payload(string wrapper) =>
            "{\"" + wrapper + "\":{" +
            $"\"employeeNo\":\"{employeeNo}\"," +
            $"\"enableCardReader\":[{readers}]," +
            $"\"fingerPrintID\":{fp.FingerPrintId}," +
            $"\"fingerType\":\"{fp.FingerType}\"," +
            $"\"fingerData\":\"{fp.FingerData}\"" +
            "}}";

        // O endpoint/método/nó da gravação de digitais varia com o firmware.
        // Tenta as combinações conhecidas até uma ser aceite (cada tentativa só
        // corre se a anterior NÃO foi aceite, por isso não duplica).
        var attempts = new (HttpMethod method, string path, string node)[]
        {
            (HttpMethod.Post, "/ISAPI/AccessControl/FingerPrint?format=json", "FingerPrint"),
            (HttpMethod.Post, "/ISAPI/AccessControl/FingerPrint?format=json", "FingerPrintCfg"),
            (HttpMethod.Put,  "/ISAPI/AccessControl/FingerPrintModify?format=json", "FingerPrintCfg"),
            (HttpMethod.Put,  "/ISAPI/AccessControl/FingerPrintModify?format=json", "FingerPrint"),
        };

        foreach (var (method, path, node) in attempts)
        {
            var json = await SendAsync(method, path, Payload(node), ct);
            if (json is not null && HikvisionUserWriteClient.ResponseIsOk(json))
                return true;
        }
        return false;
    }

    // ------------------------------------------------------------------
    // Cartões
    // ------------------------------------------------------------------

    public sealed record CardEntry(string CardNo, string CardType);

    public async Task<List<CardEntry>> GetCardsAsync(string employeeNo, CancellationToken ct)
    {
        var body =
            "{\"CardInfoSearchCond\":{" +
            "\"searchID\":\"SIBHIK\"," +
            "\"maxResults\":50," +
            "\"searchResultPosition\":0," +
            $"\"EmployeeNoList\":[{{\"employeeNo\":\"{employeeNo}\"}}]" +
            "}}";

        var json = await PostAsync("/ISAPI/AccessControl/CardInfo/Search?format=json", body, ct);
        var list = new List<CardEntry>();
        if (json is null) return list;

        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var obj in FindObjectsWith(doc.RootElement, "cardNo"))
            {
                var no = GetString(obj, "cardNo");
                if (string.IsNullOrWhiteSpace(no)) continue;
                var type = GetString(obj, "cardType") ?? "normalCard";
                list.Add(new CardEntry(no!, type));
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"{_device.DisplayName}: nao consegui ler os cartoes de {employeeNo}: {ex.Message}");
        }
        return list;
    }

    public async Task<bool> AddCardAsync(string employeeNo, CardEntry card, CancellationToken ct)
    {
        var body =
            "{\"CardInfo\":{" +
            $"\"employeeNo\":\"{employeeNo}\"," +
            $"\"cardNo\":\"{card.CardNo}\"," +
            $"\"cardType\":\"{card.CardType}\"" +
            "}}";

        var json = await PostAsync("/ISAPI/AccessControl/CardInfo/Record?format=json", body, ct);
        return json is not null && HikvisionUserWriteClient.ResponseIsOk(json);
    }

    // ------------------------------------------------------------------
    // Face (biblioteca facial FDLib)
    // ------------------------------------------------------------------

    /// <summary>Descobre o ID da biblioteca facial (FDID). Por omissão "1".</summary>
    public async Task<string> GetFaceLibIdAsync(CancellationToken ct)
    {
        var json = await GetAsync("/ISAPI/Intelligent/FDLib?format=json", ct);
        if (json is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var obj in FindObjectsWith(doc.RootElement, "FDID"))
                {
                    var id = GetString(obj, "FDID");
                    if (!string.IsNullOrWhiteSpace(id)) return id!;
                }
            }
            catch { /* usa o valor por omissão */ }
        }
        return "1";
    }

    /// <summary>Vai buscar a imagem da face gravada num número (ou null se não houver).</summary>
    public async Task<byte[]?> DownloadFaceAsync(string employeeNo, string fdid, CancellationToken ct)
    {
        var body =
            "{\"searchResultPosition\":0," +
            "\"maxResults\":10," +
            "\"faceLibType\":\"blackFD\"," +
            $"\"FDID\":\"{fdid}\"," +
            $"\"FPID\":\"{employeeNo}\"}}";

        var json = await PostAsync("/ISAPI/Intelligent/FDLib/FDSearch?format=json", body, ct);
        if (json is null) return null;

        string? faceUrl = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var obj in FindObjectsWith(doc.RootElement, "faceURL"))
            {
                faceUrl = GetString(obj, "faceURL");
                if (!string.IsNullOrWhiteSpace(faceUrl)) break;
            }
        }
        catch { /* sem face */ }

        if (string.IsNullOrWhiteSpace(faceUrl))
            return null;

        try
        {
            // O faceURL costuma ser um URL completo para o próprio equipamento.
            var target = faceUrl!.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? faceUrl!
                : _device.BaseUrl + (faceUrl!.StartsWith('/') ? faceUrl : "/" + faceUrl);
            using var resp = await _http.GetAsync(target, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.Warn($"{_device.DisplayName}: faceURL de {employeeNo} devolveu HTTP {(int)resp.StatusCode}.");
                return null;
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length > 0 ? bytes : null;
        }
        catch (Exception ex)
        {
            _log.Warn($"{_device.DisplayName}: nao consegui descarregar a face de {employeeNo}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Grava uma imagem de face num número de utilizador (multipart).</summary>
    public async Task<bool> AddFaceAsync(string employeeNo, string fdid, byte[] jpeg, string? name, CancellationToken ct)
    {
        // Metadados minimos e documentados (sem "name", que so' complica o parser
        // do terminal). A face fica ligada ao utilizador pelo FPID.
        var meta =
            "{\"faceLibType\":\"blackFD\"," +
            $"\"FDID\":\"{fdid}\"," +
            $"\"FPID\":\"{employeeNo}\"}}";

        // IMPORTANTE: o terminal Hikvision e' exigente com o formato do multipart.
        // O MultipartFormDataContent do .NET escreve os cabecalhos por uma ordem
        // (Content-Type antes de Content-Disposition) que o parser do terminal nao
        // aceita, e devolve "badJsonFormat". Por isso montamos o corpo a' mao, com
        // o Content-Disposition primeiro e os nomes entre aspas, tal como o
        // equipamento espera.
        const string boundary = "----SIBHIKfaceBoundary8f2a1c";
        const string nl = "\r\n";
        var head = new StringBuilder();
        head.Append("--").Append(boundary).Append(nl);
        head.Append("Content-Disposition: form-data; name=\"FaceDataRecord\"").Append(nl);
        head.Append("Content-Type: application/json").Append(nl).Append(nl);
        head.Append(meta).Append(nl);
        head.Append("--").Append(boundary).Append(nl);
        head.Append("Content-Disposition: form-data; name=\"img\"; filename=\"").Append(employeeNo).Append(".jpg\"").Append(nl);
        head.Append("Content-Type: image/jpeg").Append(nl).Append(nl);

        var preamble = Encoding.UTF8.GetBytes(head.ToString());
        var epilogue = Encoding.UTF8.GetBytes(nl + "--" + boundary + "--" + nl);
        var body = new byte[preamble.Length + jpeg.Length + epilogue.Length];
        Buffer.BlockCopy(preamble, 0, body, 0, preamble.Length);
        Buffer.BlockCopy(jpeg, 0, body, preamble.Length, jpeg.Length);
        Buffer.BlockCopy(epilogue, 0, body, preamble.Length + jpeg.Length, epilogue.Length);

        var content = new ByteArrayContent(body);
        content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + boundary);

        // O terminal (V4.38) rejeita PUT neste endpoint ("methodNotAllowed"):
        // a gravação da face é por POST.
        var url = _device.BaseUrl + "/ISAPI/Intelligent/FDLib/FaceDataRecord?format=json";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            using var resp = await _http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            _log.Info($"{_device.DisplayName}: FaceDataRecord FPID={employeeNo} (HTTP {(int)resp.StatusCode}) -> {Trunc(json)}");
            return resp.IsSuccessStatusCode && HikvisionUserWriteClient.ResponseIsOk(json);
        }
        catch (Exception ex)
        {
            _log.Warn($"{_device.DisplayName}: nao consegui gravar a face em {employeeNo}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Quantas faces estão gravadas num número (para verificação).</summary>
    public async Task<int> CountFacesAsync(string employeeNo, string fdid, CancellationToken ct)
    {
        var body =
            "{\"searchResultPosition\":0," +
            "\"maxResults\":10," +
            "\"faceLibType\":\"blackFD\"," +
            $"\"FDID\":\"{fdid}\"," +
            $"\"FPID\":\"{employeeNo}\"}}";

        var json = await PostAsync("/ISAPI/Intelligent/FDLib/FDSearch?format=json", body, ct);
        if (json is null) return 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            // Preferir o contador explícito; senão, contar objetos com FPID/faceURL.
            foreach (var obj in FindObjectsWith(doc.RootElement, "numOfMatches"))
            {
                var n = GetInt(obj, "numOfMatches");
                if (n.HasValue) return n.Value;
            }
            return FindObjectsWith(doc.RootElement, "faceURL").Count();
        }
        catch { return 0; }
    }

    // ------------------------------------------------------------------
    // Apagar o utilizador antigo (só depois de confirmar a migração)
    // ------------------------------------------------------------------

    public async Task<bool> DeleteUserAsync(string employeeNo, CancellationToken ct)
    {
        var body =
            "{\"UserInfoDelCond\":{" +
            $"\"EmployeeNoList\":[{{\"employeeNo\":\"{employeeNo}\"}}]" +
            "}}";

        var json = await PutAsync("/ISAPI/AccessControl/UserInfo/Delete?format=json", body, ct);
        return json is not null && HikvisionUserWriteClient.ResponseIsOk(json);
    }

    // ------------------------------------------------------------------
    // Auxiliares HTTP + JSON
    // ------------------------------------------------------------------

    private async Task<string?> PostAsync(string path, string body, CancellationToken ct)
        => await SendAsync(HttpMethod.Post, path, body, ct);

    private async Task<string?> PutAsync(string path, string body, CancellationToken ct)
        => await SendAsync(HttpMethod.Put, path, body, ct);

    private async Task<string?> SendAsync(HttpMethod method, string path, string body, CancellationToken ct)
    {
        var url = _device.BaseUrl + path;
        try
        {
            using var req = new HttpRequestMessage(method, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var resp = await _http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            _log.Info($"{_device.DisplayName}: {method} {path} -> HTTP {(int)resp.StatusCode} {Trunc(json)}");
            return json;
        }
        catch (Exception ex)
        {
            _log.Warn($"{_device.DisplayName}: {method} {path} falhou: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> GetAsync(string path, CancellationToken ct)
    {
        var url = _device.BaseUrl + path;
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            return resp.IsSuccessStatusCode ? json : null;
        }
        catch { return null; }
    }

    private static string Trunc(string s) => s.Length <= 400 ? s : s[..400] + "…";

    /// <summary>Procura recursivamente todos os objetos JSON que têm a propriedade indicada.</summary>
    private static IEnumerable<JsonElement> FindObjectsWith(JsonElement el, string prop)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty(prop, out _))
                yield return el;
            foreach (var p in el.EnumerateObject())
                foreach (var r in FindObjectsWith(p.Value, prop))
                    yield return r;
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                foreach (var r in FindObjectsWith(item, prop))
                    yield return r;
        }
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

    private static List<int> GetIntArray(JsonElement obj, string name)
    {
        var list = new List<int>();
        if (obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in v.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n))
                    list.Add(n);
        }
        return list;
    }

    public void Dispose() => _http.Dispose();
}
