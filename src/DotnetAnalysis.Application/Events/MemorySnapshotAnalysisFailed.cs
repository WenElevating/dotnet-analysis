using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;

namespace DotnetAnalysis.Application.Events;

public sealed record MemorySnapshotAnalysisFailed : IApplicationEvent
{
    public MemorySnapshotAnalysisFailed(
        ProcessDiagnosticsSessionId sessionId,
        MemorySnapshotId snapshotId,
        DiagnosticsErrorCode errorCode,
        string message,
        DateTimeOffset occurredAt,
        string source)
    {
        SessionId = sessionId;
        SnapshotId = snapshotId;
        ErrorCode = errorCode;
        Message = message;
        OccurredAt = occurredAt;
        Source = source;
    }

    public ProcessDiagnosticsSessionId SessionId { get; }

    public MemorySnapshotId SnapshotId { get; }

    public DiagnosticsErrorCode ErrorCode { get; }

    public string Message { get; }

    public DateTimeOffset OccurredAt { get; }

    public string Source { get; }

    ProcessDiagnosticsSessionId? IApplicationEvent.SessionId => SessionId;
}
