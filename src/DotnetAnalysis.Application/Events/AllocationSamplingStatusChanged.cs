using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;

namespace DotnetAnalysis.Application.Events;

public sealed record AllocationSamplingStatusChanged(
    ProcessDiagnosticsSessionId SessionId,
    string Status,
    DateTimeOffset OccurredAt,
    string Source) : IApplicationEvent, IApplicationEventDeliveryPolicy
{
    ProcessDiagnosticsSessionId? IApplicationEvent.SessionId => SessionId;

    public ApplicationEventDeliveryMode DeliveryMode => ApplicationEventDeliveryMode.LatestOnly;

    public string DeliveryKey => $"{SessionId.Value:N}:allocation-sampling";
}
