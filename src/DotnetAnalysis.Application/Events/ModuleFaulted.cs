using DotnetAnalysis.Core.Events;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Application.Events;

public sealed record ModuleFaulted(
    ProcessDiagnosticsSessionId? SessionId,
    string Module,
    string Message,
    DateTimeOffset OccurredAt,
    string Source) : IApplicationEvent;
