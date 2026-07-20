namespace HikvisionSqlBridge.Core.Model;

/// <summary>
/// Uma linha da tabela TG_MOVIMENTOS, já com os valores exactamente no formato
/// que o sistema de assiduidade espera. Os valores fixos (0, "I", NULL) seguem
/// a especificação do cliente.
/// </summary>
public sealed class Movimento
{
    // ID              -> identity, automático (não é escrito por nós)

    /// <summary>Fica a 0 no INSERT. O trigger [dbo].[Movimentos_INSERT] preenche-o
    /// automaticamente a seguir, a partir de TA_IDENTIFICADORES.</summary>
    public double IdNumero { get; set; }

    /// <summary>Data/hora da picagem (datetime).</summary>
    public DateTime IdDataHora { get; set; }

    public int IdMainCode { get; set; } = 0;

    /// <summary>Código do método de identificação (1=RFID, 2=Digital/Face, 3=PIN, 5=Matrícula, 6=NFC, 7=QR).</summary>
    public int IdTipoIdentificador { get; set; }

    /// <summary>Número do utilizador em texto, com zeros à esquerda até 5 dígitos ("00001").</summary>
    public string IdIdentificador { get; set; } = "";

    /// <summary>"I" = Indiferenciado (sem distinção entrada/saída).</summary>
    public string IdTipo { get; set; } = "I";

    public string IdSupportCode { get; set; } = "0";

    /// <summary>IP do equipamento.</summary>
    public string IdIpTerminal { get; set; } = "";

    public bool IdEnd { get; set; } = false;

    // ID_LATITUDE / ID_LONGITUDE / ID_UTILIZADOR -> NULL
    // ID_DATA_SISTEMA -> automático (default getdate())

    public string IdCodClassificacao { get; set; } = "0";
}
