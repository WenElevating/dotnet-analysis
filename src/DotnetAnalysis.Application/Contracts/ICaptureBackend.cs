using DotnetAnalysis.Core.Sessions;

namespace DotnetAnalysis.Application.Contracts;

public interface ICaptureBackend
{
    Task StartAllocationTraceAsync(AnalysisSession session, CancellationToken cancellationToken);

    Task StopAllocationTraceAsync(AnalysisSession session, CancellationToken cancellationToken);

    Task CaptureHeapSnapshotAsync(AnalysisSession session, CancellationToken cancellationToken);

    /// <summary>
    /// Promptly and idempotently escalates cancellation of active backend work.
    /// This call can overlap cancellation-token signaling.
    /// </summary>
    Task CancelAsync(AnalysisSession session, CancellationToken cancellationToken);
}
