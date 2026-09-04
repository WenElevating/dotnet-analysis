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
}
