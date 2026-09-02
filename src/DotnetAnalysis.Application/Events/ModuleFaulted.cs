using DotnetAnalysis.Core.Events;
using DotnetAnalysis.Core.Sessions;

namespace DotnetAnalysis.Application.Events;

public sealed record ModuleFaulted(
    SessionId? SessionId,
    string Module,
    string Message,
    DateTimeOffset OccurredAt,
    string Source) : IApplicationEvent;
