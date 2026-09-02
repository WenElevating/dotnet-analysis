using DotnetAnalysis.Core.Events;
using DotnetAnalysis.Core.Sessions;

namespace DotnetAnalysis.Application.Events;

public sealed record AnalysisCompleted(
    SessionId? SessionId,
    DateTimeOffset OccurredAt,
    string Source) : IApplicationEvent;
