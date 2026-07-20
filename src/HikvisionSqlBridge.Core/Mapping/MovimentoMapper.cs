using HikvisionSqlBridge.Core.Model;

namespace HikvisionSqlBridge.Core.Mapping;

/// <summary>
/// Converte uma picagem normalizada (<see cref="AccessEvent"/>) numa linha de
/// TG_MOVIMENTOS (<see cref="Movimento"/>), seguindo a especificação do cliente.
/// </summary>
public static class MovimentoMapper
{
    /// <summary>
    /// Códigos de ID_TIPO_IDENTIFICADOR conforme definido pelo cliente:
    /// 1=RFID, 2=Impressão digital OU Face, 3=PIN, 5=Matrícula, 6=NFC, 7=QR Code.
    /// </summary>
    public static int ToTipoIdentificador(VerifyMethod method) => method switch
    {
        VerifyMethod.Card => 1,
        VerifyMethod.Fingerprint => 2,
        VerifyMethod.Face => 2,
        VerifyMethod.Pin => 3,
        VerifyMethod.LicensePlate => 5,
        VerifyMethod.Nfc => 6,
        VerifyMethod.QrCode => 7,
        // Método desconhecido: assume 2 (digital/face), que é o valor por
        // omissão do campo na tabela.
        _ => 2,
    };

    /// <summary>
    /// Formata o número do utilizador com zeros à esquerda até 5 dígitos
    /// (1 -> "00001", 489 -> "00489"). Se já tiver mais de 5 dígitos, é
    /// mantido tal como está.
    /// </summary>
    public static string FormatIdentificador(string employeeNo)
    {
        var trimmed = (employeeNo ?? "").Trim();
        if (trimmed.Length == 0) return "00000";

        // Se for puramente numérico, remove zeros supérfluos e volta a preencher
        // até 5 dígitos, para ficar sempre no formato "00489".
        if (long.TryParse(trimmed, out var n) && n >= 0)
            return n.ToString().PadLeft(5, '0');

        // Caso não seja numérico (raro), preenche à esquerda na mesma.
        return trimmed.PadLeft(5, '0');
    }

    public static Movimento ToMovimento(AccessEvent e)
    {
        return new Movimento
        {
            // ID_NUMERO fica a 0 — o trigger [dbo].[Movimentos_INSERT] preenche-o
            // depois do INSERT, cruzando (ID_TIPO_IDENTIFICADOR, ID_IDENTIFICADOR)
            // com a tabela TA_IDENTIFICADORES.
            IdNumero = 0,
            IdDataHora = e.EventTime,
            IdMainCode = 0,
            IdTipoIdentificador = ToTipoIdentificador(e.Method),
            IdIdentificador = FormatIdentificador(e.EmployeeNo),
            IdTipo = "I",
            IdSupportCode = "0",
            IdIpTerminal = e.TerminalIp,
            IdEnd = false,
            IdCodClassificacao = "0",
        };
    }
}
