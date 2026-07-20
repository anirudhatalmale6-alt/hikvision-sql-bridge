namespace HikvisionSqlBridge.Core.Hikvision;

/// <summary>
/// Decide se uma picagem foi acesso concedido ou negado, a partir dos códigos
/// majorEventType/subEventType do evento Hikvision.
///
/// Os códigos "minor" de negação variam com o firmware. Mantemos aqui a lista
/// dos casos de negação conhecidos; qualquer evento com utilizador que não
/// esteja nessa lista é tratado como concedido. Durante os testes com o
/// equipamento real confirmamos/ajustamos esta lista com os códigos exactos
/// que o vosso terminal envia.
/// </summary>
public static class AccessResultClassifier
{
    // majorEventType de controlo de acessos (a Hikvision usa 5 = "Event").
    private const int MajorAccessEvent = 5;

    // subEventType (minor) conhecidos como NEGAÇÃO de acesso.
    // (cartão expirado, fora de horário, anti-passback, autenticação falhada, etc.)
    // Ajustado durante os testes com o equipamento.
    private static readonly HashSet<int> DeniedMinorTypes = new()
    {
        // Exemplos habituais — a confirmar com o terminal real:
        // 22,  // cartão não existe
        // 23,  // cartão expirado
        // 27,  // fora do período válido
        // 47,  // autenticação por face falhou
        // 48,  // autenticação por impressão digital falhou
    };

    public static bool IsGranted(int majorEventType, int minorEventType)
    {
        // Se não temos códigos válidos, assumimos concedido (há um utilizador
        // associado ao evento). É o comportamento seguro por omissão.
        if (majorEventType < 0 || minorEventType < 0)
            return true;

        if (majorEventType == MajorAccessEvent && DeniedMinorTypes.Contains(minorEventType))
            return false;

        return true;
    }
}
