using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;

namespace DotnetAnalysis.Application.Events;

public sealed record ProcessDiagnosticsSessionEnded(
    ProcessDiagnosticsSessionId SessionId,
    DateTimeOffset OccurredAt,
    string Source) : IApplicationEvent
{
    ProcessDiagnosticsSessionId? IApplicationEvent.SessionId => SessionId;
}
