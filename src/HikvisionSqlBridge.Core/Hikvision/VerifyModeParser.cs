using HikvisionSqlBridge.Core.Model;

namespace HikvisionSqlBridge.Core.Hikvision;

/// <summary>
/// Traduz o método de verificação reportado pelo terminal Hikvision (campo
/// currentVerifyMode / verifyMode do evento) para o nosso <see cref="VerifyMethod"/>.
///
/// Os valores da Hikvision variam com o modelo/firmware e por vezes vêm
/// combinados (ex.: "cardOrFaceOrFp"). Aqui damos prioridade ao método mais
/// específico. Durante os testes com o equipamento real afinamos esta tabela
/// com os valores exactos que o vosso terminal envia.
/// </summary>
public static class VerifyModeParser
{
    public static VerifyMethod Parse(string? verifyMode)
    {
        if (string.IsNullOrWhiteSpace(verifyMode))
            return VerifyMethod.Unknown;

        var v = verifyMode.Trim().ToLowerInvariant();

        // Métodos específicos primeiro (a ordem importa para modos combinados).
        if (Contains(v, "qr")) return VerifyMethod.QrCode;
        if (Contains(v, "plate", "matricula", "matrícula", "anpr", "vehicle"))
            return VerifyMethod.LicensePlate;
        if (Contains(v, "nfc")) return VerifyMethod.Nfc;
        if (Contains(v, "face")) return VerifyMethod.Face;
        if (Contains(v, "fp", "finger")) return VerifyMethod.Fingerprint;
        if (Contains(v, "pw", "pin", "keypad", "password", "employeeno"))
            return VerifyMethod.Pin;
        if (Contains(v, "card", "rfid", "ic")) return VerifyMethod.Card;

        return VerifyMethod.Unknown;
    }

    private static bool Contains(string value, params string[] needles)
    {
        foreach (var n in needles)
            if (value.Contains(n, StringComparison.Ordinal))
                return true;
        return false;
    }
}
