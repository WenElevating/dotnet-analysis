using DotnetAnalysis.Core.Sessions;

namespace DotnetAnalysis.Application.Contracts;

public interface ICaptureBackend
{
    Task StartAllocationTraceAsync(AnalysisSession session, CancellationToken cancellationToken);

    Task StopAllocationTraceAsync(AnalysisSession session, CancellationToken cancellationToken);

    Task CaptureHeapSnapshotAsync(AnalysisSession session, CancellationToken cancellationToken);

    Task CancelAsync(AnalysisSession session, CancellationToken cancellationToken);
}
