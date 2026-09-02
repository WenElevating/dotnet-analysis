using DotnetAnalysis.Application.Contracts.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

public static class DiagnosticsExceptionFactory
{
    public static DiagnosticsException CaptureFailed(Exception exception) =>
        new(DiagnosticsErrorCode.CaptureFailed, "Diagnostics capture failed.", exception);

    public static DiagnosticsException TargetExited(Exception exception) =>
        new(DiagnosticsErrorCode.TargetExited, "The target process exited.", exception);
}
