using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Orchestration.Models;
using DotnetAnalysis.Orchestration.Operations;

namespace DotnetAnalysis.Orchestration;

/// <summary>
/// 表示宿主可观察、拥有底层诊断会话生命周期的活动分析会话。
/// </summary>
public interface IAnalysisSession : IAsyncDisposable
{
    /// <summary>会话绑定的目标上下文。</summary>
    TargetContext Target { get; }

    /// <summary>底层会话的稳定身份。</summary>
    ProcessDiagnosticsSessionId Id { get; }

    /// <summary>当前会话状态。</summary>
    ProcessDiagnosticsSessionState State { get; }

    /// <summary>目标能力摘要。</summary>
    DiagnosticCapabilities Capabilities { get; }

    /// <summary>当前内存时间线。</summary>
    MemoryTimeline Timeline { get; }

    /// <summary>时间线质量摘要。</summary>
    DiagnosticQualitySummary Quality { get; }

    /// <summary>最近一次稳定失败信息。</summary>
    DiagnosticFailure? Failure { get; }

    /// <summary>按指定证据级别捕获快照。</summary>
    /// <param name="captureMode">请求的捕获方式。</param>
    /// <param name="cancellationToken">取消本次捕获的令牌。</param>
    /// <returns>已持久化的快照描述。</returns>
    Task<MemorySnapshot> CaptureAsync(MemorySnapshotCaptureMode captureMode, CancellationToken cancellationToken);

    /// <summary>查询当前会话时间线范围内的执行采样。</summary>
    /// <param name="timeRange">需要查询的 UTC 时间区间。</param>
    /// <param name="queryMode">执行采样读取方式。</param>
    /// <param name="cancellationToken">取消本次查询的令牌。</param>
    /// <returns>执行采样分析结果。</returns>
    Task<ExecutionProfile> GetExecutionProfileAsync(ExecutionTimeRange timeRange, ExecutionProfileQueryMode queryMode, CancellationToken cancellationToken);

    /// <summary>停止会话并等待所有后台资源收尾。</summary>
    /// <param name="cancellationToken">取消等待收尾的令牌。</param>
    Task StopAsync(CancellationToken cancellationToken);
}
