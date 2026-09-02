using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;

namespace DotnetAnalysis.Application.Events;

public sealed record MemorySnapshotCaptureFailed : IApplicationEvent
{
    public MemorySnapshotCaptureFailed(
        ProcessDiagnosticsSessionId sessionId,
        DiagnosticsErrorCode errorCode,
        string message,
        DateTimeOffset occurredAt,
        string source)
    {
        SessionId = sessionId;
        ErrorCode = errorCode;
        Message = message;
        OccurredAt = occurredAt;
        Source = source;
    }

    public ProcessDiagnosticsSessionId SessionId { get; }

    public DiagnosticsErrorCode ErrorCode { get; }

    public string Message { get; }

    public DateTimeOffset OccurredAt { get; }

    public string Source { get; }

    ProcessDiagnosticsSessionId? IApplicationEvent.SessionId => SessionId;
}
