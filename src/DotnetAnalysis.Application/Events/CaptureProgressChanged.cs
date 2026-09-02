using DotnetAnalysis.Core.Events;
using DotnetAnalysis.Core.Sessions;

namespace DotnetAnalysis.Application.Events;

public sealed record CaptureProgressChanged(
    SessionId? SessionId,
    int Percent,
    DateTimeOffset OccurredAt,
    string Source) : IApplicationEvent;
