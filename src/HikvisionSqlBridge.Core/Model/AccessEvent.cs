namespace HikvisionSqlBridge.Core.Model;

/// <summary>
/// Uma picagem normalizada, já independente do formato do Hikvision.
/// É isto que depois é mapeado para a tabela TG_MOVIMENTOS.
/// </summary>
public sealed class AccessEvent
{
    /// <summary>Data/hora da picagem, tal como reportada pelo terminal.</summary>
    public DateTime EventTime { get; set; }

    /// <summary>Número do utilizador (employeeNo) vindo do terminal.</summary>
    public string EmployeeNo { get; set; } = "";

    /// <summary>Método de verificação normalizado (cartão, digital, face, PIN, ...).</summary>
    public VerifyMethod Method { get; set; } = VerifyMethod.Unknown;

    /// <summary>IP do equipamento onde a picagem foi efectuada.</summary>
    public string TerminalIp { get; set; } = "";

    /// <summary>True se o acesso foi concedido; false se foi negado.</summary>
    public bool Granted { get; set; }

    /// <summary>Número de série do terminal, quando disponível (só para logs).</summary>
    public string? SerialNo { get; set; }

    public override string ToString() =>
        $"[{EventTime:yyyy-MM-dd HH:mm:ss}] user={EmployeeNo} method={Method} " +
        $"granted={Granted} terminal={TerminalIp}";
}

/// <summary>Método de verificação usado na picagem.</summary>
public enum VerifyMethod
{
    Unknown = 0,
    Card,        // RFID
    Fingerprint, // Impressão digital
    Face,
    Pin,         // Teclado
    Nfc,
    QrCode,
    LicensePlate // Matrícula de carro
}
