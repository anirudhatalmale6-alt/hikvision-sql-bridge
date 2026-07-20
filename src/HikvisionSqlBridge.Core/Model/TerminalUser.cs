namespace HikvisionSqlBridge.Core.Model;

/// <summary>
/// Um utilizador inscrito no terminal Hikvision (lido por ISAPI). É a partir
/// disto que criamos/atualizamos a ficha em TG_FUNCIONARIOS e os identificadores
/// em TA_IDENTIFICADORES.
/// </summary>
public sealed class TerminalUser
{
    /// <summary>Número do funcionário (employeeNo) definido no iVMS.</summary>
    public string EmployeeNo { get; set; } = "";

    /// <summary>Nome do funcionário.</summary>
    public string Name { get; set; } = "";

    public DateTime? ValidBegin { get; set; }
    public DateTime? ValidEnd { get; set; }

    /// <summary>Tem pelo menos um cartão associado (=> identificador tipo 1).</summary>
    public bool HasCard { get; set; }

    /// <summary>Tem impressão digital ou face (=> identificador tipo 2).</summary>
    public bool HasFingerprintOrFace { get; set; }

    /// <summary>Tem PIN/código de teclado (=> identificador tipo 3).</summary>
    public bool HasPin { get; set; }

    public override string ToString() =>
        $"employeeNo={EmployeeNo} nome=\"{Name}\" cartao={HasCard} digital/face={HasFingerprintOrFace}";
}
