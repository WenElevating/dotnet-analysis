using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

public sealed class GCDumpSnapshotCollector
{
    public static Task<(string TemporaryPath, DateTimeOffset CapturedAtUtc)> CaptureAsync(
        TargetProcess target,
        SnapshotStorageLayout layout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new DiagnosticsException(
            DiagnosticsErrorCode.RuntimeNotSupported,
            "Live gcdump capture requires a supported EventPipe runtime adapter.");
    }
}
