using DotnetAnalysis.Core.Events;
using DotnetAnalysis.Core.Sessions;

namespace DotnetAnalysis.Application.Events;

public sealed record AnalysisFailed(
    SessionId? SessionId,
    string FailureCode,
    string Message,
    DateTimeOffset OccurredAt,
    string Source) : IApplicationEvent;
