using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;

namespace DotnetAnalysis.Application.Events;

public sealed record MemorySnapshotCaptureStarted(
    ProcessDiagnosticsSessionId SessionId,
    DateTimeOffset OccurredAt,
    string Source) : IApplicationEvent
{
    ProcessDiagnosticsSessionId? IApplicationEvent.SessionId => SessionId;
}
