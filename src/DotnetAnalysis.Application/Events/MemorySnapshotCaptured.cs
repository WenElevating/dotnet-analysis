using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;

namespace DotnetAnalysis.Application.Events;

public sealed record MemorySnapshotCaptured(
    ProcessDiagnosticsSessionId SessionId,
    MemorySnapshotId SnapshotId,
    DateTimeOffset OccurredAt,
    string Source) : IApplicationEvent
{
    ProcessDiagnosticsSessionId? IApplicationEvent.SessionId => SessionId;
}
