namespace DotnetAnalysis.Application.Contracts.Diagnostics;

/// <summary>
/// 携带稳定诊断错误码的应用层异常。
/// </summary>
public sealed class DiagnosticsException : Exception
{
    /// <summary>
    /// 创建诊断异常。
    /// </summary>
    /// <param name="errorCode">稳定错误分类。</param>
    /// <param name="message">面向日志或上层处理的说明。</param>
    /// <param name="innerException">导致该错误的底层异常。</param>
    public DiagnosticsException(
        DiagnosticsErrorCode errorCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// 稳定错误分类，调用方可据此决定重试或展示文案。
    /// </summary>
    public DiagnosticsErrorCode ErrorCode { get; }
}
