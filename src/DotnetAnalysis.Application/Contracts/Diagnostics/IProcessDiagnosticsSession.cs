using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Application.Contracts.Diagnostics;

/// <summary>
/// 表示一个可采样、可捕获快照并可异步结束的诊断会话。
/// </summary>
public interface IProcessDiagnosticsSession : IAsyncDisposable
{
    /// <summary>
    /// 会话稳定标识。
    /// </summary>
    ProcessDiagnosticsSessionId Id { get; }

    /// <summary>
    /// 会话绑定的目标进程。
    /// </summary>
    TargetProcess Process { get; }

    /// <summary>
    /// 会话生命周期状态。
    /// </summary>
    ProcessDiagnosticsSessionState State { get; }

    /// <summary>
    /// 请求结束会话；实现应取消后台采样。
    /// </summary>
    Task EndAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 异步枚举内存使用样本，直到取消或会话结束。
    /// </summary>
    IAsyncEnumerable<MemoryUsageSample> GetMemoryUsageAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// 捕获一次快照；失败或取消时返回稳定诊断异常。
    /// </summary>
    Task<MemorySnapshot> CaptureSnapshotAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// 使用指定证据级别捕获一次快照；保留分析方式不会静默降级为轻量堆快照。
    /// </summary>
    /// <param name="captureMode">请求的快照捕获方式。</param>
    /// <param name="cancellationToken">取消当前捕获的令牌。</param>
    /// <returns>已被持久化、等待分析的快照描述。</returns>
    /// <exception cref="DiagnosticsException">所选捕获方式不可用、目标身份变化、捕获失败或捕获取消时引发。</exception>
    Task<MemorySnapshot> CaptureSnapshotAsync(
        MemorySnapshotCaptureMode captureMode,
        CancellationToken cancellationToken) => captureMode is MemorySnapshotCaptureMode.Standard
            ? CaptureSnapshotAsync(cancellationToken)
            : Task.FromException<MemorySnapshot>(new DiagnosticsException(
                DiagnosticsErrorCode.ProfilerAttachUnavailable,
                "当前诊断会话不支持保留函数分析捕获。"));

    /// <summary>
    /// 查询指定时间区间内的托管执行采样分析结果。
    /// </summary>
    /// <param name="timeRange">需要查询的 UTC 执行采样时间区间。</param>
    /// <param name="cancellationToken">取消本次查询等待的标记。</param>
    /// <returns>指定时间区间内的执行采样分析结果。</returns>
    /// <exception cref="DiagnosticsException">执行采样不可用、指定区间不可查询或读取结果失败时引发。</exception>
    Task<ExecutionProfile> GetExecutionProfileAsync(
        ExecutionTimeRange timeRange,
        CancellationToken cancellationToken) => GetExecutionProfileAsync(
            timeRange,
            ExecutionProfileQueryMode.Incremental,
            cancellationToken);

    /// <summary>
    /// 使用指定读取方式查询指定时间区间内的托管执行采样分析结果。
    /// </summary>
    /// <param name="timeRange">需要查询的 UTC 执行采样时间区间。</param>
    /// <param name="queryMode">执行分析读取方式。</param>
    /// <param name="cancellationToken">取消本次查询等待的标记。</param>
    /// <returns>指定时间区间内的执行采样分析结果。</returns>
    /// <exception cref="DiagnosticsException">执行采样不可用、指定区间不可查询或读取结果失败时引发。</exception>
    Task<ExecutionProfile> GetExecutionProfileAsync(
        ExecutionTimeRange timeRange,
        ExecutionProfileQueryMode queryMode,
        CancellationToken cancellationToken) => Task.FromException<ExecutionProfile>(
            new DiagnosticsException(
                DiagnosticsErrorCode.ExecutionProfilingUnavailable,
                "当前诊断会话不支持执行采样查询。"));
}
