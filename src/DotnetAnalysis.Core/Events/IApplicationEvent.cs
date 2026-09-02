using DotnetAnalysis.Core.Sessions;

namespace DotnetAnalysis.Core.Events;

public interface IApplicationEvent
{
    DateTimeOffset OccurredAt { get; }

    SessionId? SessionId { get; }

    string Source { get; }
}
