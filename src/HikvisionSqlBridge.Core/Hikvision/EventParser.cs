using System.Globalization;
using System.Xml.Linq;
using HikvisionSqlBridge.Core.Model;

namespace HikvisionSqlBridge.Core.Hikvision;

/// <summary>
/// Faz o parse de um bloco XML "EventNotificationAlert" (formato ISAPI da
/// Hikvision) para o nosso <see cref="AccessEvent"/>. É namespace-agnóstico:
/// procura os elementos pelo nome local, para funcionar com as pequenas
/// variações entre modelos/firmwares.
/// </summary>
public static class EventParser
{
    /// <summary>
    /// Tenta fazer o parse. Devolve false (e evento a null) quando o XML não é
    /// um evento de controlo de acessos — por exemplo heartbeats ou outros
    /// tipos de evento que não interessam.
    /// </summary>
    public static bool TryParse(string xml, string fallbackTerminalIp, out AccessEvent? evt)
    {
        evt = null;
        if (string.IsNullOrWhiteSpace(xml)) return false;

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return false; }

        var root = doc.Root;
        if (root is null || !LocalName(root).Equals("EventNotificationAlert", StringComparison.OrdinalIgnoreCase))
            return false;

        var eventType = Value(root, "eventType");
        // Só nos interessam os eventos de controlo de acessos.
        if (!string.Equals(eventType, "AccessControllerEvent", StringComparison.OrdinalIgnoreCase))
            return false;

        var ace = Child(root, "AccessControllerEvent");
        if (ace is null) return false;

        var employeeNo = Value(ace, "employeeNoString") ?? Value(ace, "employeeNo");
        // Sem número de utilizador não é uma picagem de pessoa (ex.: porta
        // aberta por botão, sabotagem, etc.) — ignoramos.
        if (string.IsNullOrWhiteSpace(employeeNo)) return false;

        var terminalIp = Value(root, "ipAddress");
        if (string.IsNullOrWhiteSpace(terminalIp)) terminalIp = fallbackTerminalIp;

        var eventTime = ParseDateTime(Value(root, "dateTime"));
        var verifyMode = Value(ace, "currentVerifyMode") ?? Value(ace, "verifyMode");

        int major = ParseInt(Value(ace, "majorEventType"));
        int minor = ParseInt(Value(ace, "subEventType"));

        evt = new AccessEvent
        {
            EventTime = eventTime,
            EmployeeNo = employeeNo.Trim(),
            Method = VerifyModeParser.Parse(verifyMode),
            TerminalIp = terminalIp ?? "",
            SerialNo = Value(ace, "serialNo"),
            Granted = AccessResultClassifier.IsGranted(major, minor),
        };
        return true;
    }

    private static DateTime ParseDateTime(string? s)
    {
        if (!string.IsNullOrWhiteSpace(s) &&
            DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var dto))
        {
            // O terminal envia a sua hora local com offset; guardamos a hora
            // "de relógio" tal como aparece no equipamento.
            return dto.DateTime;
        }
        // Sem data válida usamos a hora local do servidor como último recurso.
        return DateTime.Now;
    }

    private static int ParseInt(string? s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;

    // --- Helpers de leitura de XML, ignorando namespaces ---

    private static string LocalName(XElement e) => e.Name.LocalName;

    private static XElement? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e =>
            e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

    private static string? Value(XElement parent, string localName) =>
        Child(parent, localName)?.Value?.Trim();
}
