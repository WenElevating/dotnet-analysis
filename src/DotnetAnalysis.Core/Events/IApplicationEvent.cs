using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Core.Events;

public interface IApplicationEvent
{
    DateTimeOffset OccurredAt { get; }

    ProcessDiagnosticsSessionId? SessionId { get; }

    string Source { get; }
}
