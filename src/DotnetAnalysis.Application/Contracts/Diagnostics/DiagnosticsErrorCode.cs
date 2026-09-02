namespace DotnetAnalysis.Application.Contracts.Diagnostics;

public enum DiagnosticsErrorCode
{
    AccessDenied,
    TargetExited,
    TargetChanged,
    RuntimeNotSupported,
    SnapshotFormatNotSupported,
    CaptureFailed,
    CaptureCancelled
}
