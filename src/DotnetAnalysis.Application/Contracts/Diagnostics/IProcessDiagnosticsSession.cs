using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Application.Contracts.Diagnostics;

public interface IProcessDiagnosticsSession : IAsyncDisposable
{
    ProcessDiagnosticsSessionId Id { get; }

    TargetProcess Process { get; }

    ProcessDiagnosticsSessionState State { get; }

    Task EndAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<MemoryUsageSample> GetMemoryUsageAsync(
        CancellationToken cancellationToken);

    Task<MemorySnapshot> CaptureSnapshotAsync(
        CancellationToken cancellationToken);
}
