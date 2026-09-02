using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;

namespace DotnetAnalysis.Application.Events;

public sealed record ProcessMemoryUsageUpdated(
    ProcessDiagnosticsSessionId SessionId,
    MemoryUsageSample Sample,
    DateTimeOffset OccurredAt,
    string Source) : IApplicationEvent, IApplicationEventDeliveryPolicy
{
    ProcessDiagnosticsSessionId? IApplicationEvent.SessionId => SessionId;

    public ApplicationEventDeliveryMode DeliveryMode => ApplicationEventDeliveryMode.LatestOnly;

    public string DeliveryKey => $"{SessionId.Value:N}:process-memory";
}
