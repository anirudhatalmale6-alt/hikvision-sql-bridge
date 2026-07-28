namespace HikvisionSqlBridge.Core.Hikvision;

/// <summary>
/// Regras para "acertar" números de utilizador que ficaram com zeros à esquerda
/// no terminal (ex.: gravaram "00137" à mão mas na base de dados o número é
/// "137"). Passa o identificador para a forma sem zeros, que é o alvo para onde
/// se migram as biometrias do número antigo (00137 -> 137).
/// </summary>
public static class IdRenumber
{
    /// <summary>
    /// Devolve o número sem zeros à esquerda. Só mexe em identificadores
    /// puramente numéricos; qualquer coisa com letras fica igual (por segurança,
    /// para nunca estragar IDs que não sejam só números).
    /// "00137" -> "137", "007" -> "7", "0"/"000" -> "0", "137" -> "137".
    /// </summary>
    public static string StripLeadingZeros(string? employeeNo)
    {
        var s = employeeNo?.Trim() ?? "";
        if (s.Length == 0)
            return "";
        if (!IsAllDigits(s))
            return s; // não é só dígitos -> não tocar

        var trimmed = s.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    /// <summary>
    /// True se o número tem zeros à esquerda a mais — ou seja, é numérico e
    /// diferente da forma já normalizada. "00137" -> true, "137" -> false,
    /// "0" -> false (não há nada para tirar).
    /// </summary>
    public static bool IsZeroPadded(string? employeeNo)
    {
        var s = employeeNo?.Trim() ?? "";
        if (s.Length == 0 || !IsAllDigits(s))
            return false;

        var norm = StripLeadingZeros(s);
        return norm != s;
    }

    private static bool IsAllDigits(string s)
    {
        foreach (var c in s)
            if (c < '0' || c > '9')
                return false;
        return s.Length > 0;
    }
}
