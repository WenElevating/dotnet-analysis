using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;

namespace DotnetAnalysis.Application.Events;

public sealed record ProcessDiagnosticsSessionStateChanged(
    ProcessDiagnosticsSessionId SessionId,
    ProcessDiagnosticsSessionState State,
    DateTimeOffset OccurredAt,
    string Source) : IApplicationEvent
{
    ProcessDiagnosticsSessionId? IApplicationEvent.SessionId => SessionId;
}
