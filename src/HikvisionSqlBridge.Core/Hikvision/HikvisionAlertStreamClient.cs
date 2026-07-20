using System.Net;
using System.Text;
using HikvisionSqlBridge.Core.Configuration;
using HikvisionSqlBridge.Core.Diagnostics;
using HikvisionSqlBridge.Core.Model;

namespace HikvisionSqlBridge.Core.Hikvision;

/// <summary>
/// Cliente do canal de eventos ISAPI da Hikvision. Mantém uma ligação HTTP
/// aberta a /ISAPI/Event/notification/alertStream (long-polling) e vai
/// recebendo os eventos assim que o terminal os envia. Não depende de DLLs
/// do SDK — só HTTP com autenticação Digest.
/// </summary>
public sealed class HikvisionAlertStreamClient : IDisposable
{
    private const string AlertStreamPath = "/ISAPI/Event/notification/alertStream";

    private readonly DeviceConfig _device;
    private readonly IAppLogger _log;
    private readonly HttpClient _http;

    public HikvisionAlertStreamClient(DeviceConfig device, IAppLogger log)
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
            // Sem timeout: o stream fica aberto indefinidamente.
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// Liga-se ao terminal e chama <paramref name="onEvent"/> por cada picagem.
    /// Termina (por excepção ou fim de stream) quando a ligação cai — o chamador
    /// trata da reconexão.
    /// </summary>
    public async Task ListenAsync(Func<AccessEvent, Task> onEvent, CancellationToken ct)
    {
        var url = _device.BaseUrl + AlertStreamPath;
        _log.Info($"A ligar ao terminal {_device.DisplayName} em {url}");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        _log.Info($"Ligado ao terminal {_device.DisplayName}. A aguardar picagens...");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var buffer = new StringBuilder();
        var chunk = new char[4096];

        while (!ct.IsCancellationRequested)
        {
            int read = await reader.ReadAsync(chunk, ct);
            if (read == 0) break; // stream fechado pelo equipamento
            buffer.Append(chunk, 0, read);

            foreach (var xml in ExtractEvents(buffer))
            {
                _log.Raw(_device.DisplayName, xml);
                if (EventParser.TryParse(xml, _device.Ip, out var evt) && evt is not null)
                    await onEvent(evt);
            }
        }
    }

    /// <summary>
    /// Extrai do buffer todos os blocos EventNotificationAlert completos e
    /// remove-os do buffer, deixando lá qualquer fragmento incompleto.
    /// </summary>
    internal static IEnumerable<string> ExtractEvents(StringBuilder buffer)
    {
        const string open = "<EventNotificationAlert";
        const string close = "</EventNotificationAlert>";

        var results = new List<string>();
        var text = buffer.ToString();
        int searchFrom = 0;

        while (true)
        {
            int start = text.IndexOf(open, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (start < 0) break;

            int end = text.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
            if (end < 0) break; // bloco ainda incompleto — espera por mais dados

            int endInclusive = end + close.Length;
            results.Add(text.Substring(start, endInclusive - start));
            searchFrom = endInclusive;
        }

        if (results.Count > 0)
        {
            // Mantém no buffer só o que sobra depois do último bloco completo.
            buffer.Clear();
            if (searchFrom < text.Length)
                buffer.Append(text, searchFrom, text.Length - searchFrom);
        }
        else if (buffer.Length > 1_000_000)
        {
            // Salvaguarda: se algo corre mal e nunca fecha um bloco, não
            // deixamos o buffer crescer sem limite.
            buffer.Clear();
        }

        return results;
    }

    public void Dispose() => _http.Dispose();
}
