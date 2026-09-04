using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;

namespace DotnetAnalysis.Application.Events;

/// <summary>
/// 表示一次进程内存采样更新；同一会话仅投递最新待处理样本。
/// </summary>
public sealed record ProcessMemoryUsageUpdated(
    ProcessDiagnosticsSessionId SessionId,
    MemoryUsageSample Sample,
    DateTimeOffset OccurredAt,
    string Source) : IApplicationEvent, IApplicationEventDeliveryPolicy
{
    /// <summary>
    /// 此事件关联的会话。
    /// </summary>
    ProcessDiagnosticsSessionId? IApplicationEvent.SessionId => SessionId;

    /// <summary>
    /// 使用最新样本覆盖策略。
    /// </summary>
    public ApplicationEventDeliveryMode DeliveryMode => ApplicationEventDeliveryMode.LatestOnly;

    /// <summary>
    /// 按会话区分进程内存样本流的投递键。
    /// </summary>
    public string DeliveryKey => $"{SessionId.Value:N}:process-memory";
}
