using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;

namespace DotnetAnalysis.Application.Events;

/// <summary>
/// 表示分配采样器状态变化；同一会话仅保留最新状态。
/// </summary>
public sealed record AllocationSamplingStatusChanged(
    ProcessDiagnosticsSessionId SessionId,
    string Status,
    DateTimeOffset OccurredAt,
    string Source) : IApplicationEvent, IApplicationEventDeliveryPolicy
{
    /// <summary>
    /// 此事件关联的会话。
    /// </summary>
    ProcessDiagnosticsSessionId? IApplicationEvent.SessionId => SessionId;

    /// <summary>
    /// 使用最新状态覆盖策略。
    /// </summary>
    public ApplicationEventDeliveryMode DeliveryMode => ApplicationEventDeliveryMode.LatestOnly;

    /// <summary>
    /// 按会话区分分配采样状态流的投递键。
    /// </summary>
    public string DeliveryKey => $"{SessionId.Value:N}:allocation-sampling";
}
