namespace DotnetAnalysis.Application.Contracts.Diagnostics;

public sealed class DiagnosticsException : Exception
{
    public DiagnosticsException(
        DiagnosticsErrorCode errorCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public DiagnosticsErrorCode ErrorCode { get; }
}
