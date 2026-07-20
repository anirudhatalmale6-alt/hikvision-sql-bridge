namespace HikvisionSqlBridge.Core.Diagnostics;

/// <summary>Log simples para auditoria e diagnóstico.</summary>
public interface IAppLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);

    /// <summary>Evento bruto recebido do terminal (só quando o diagnóstico está ligado).</summary>
    void Raw(string terminal, string payload);
}
