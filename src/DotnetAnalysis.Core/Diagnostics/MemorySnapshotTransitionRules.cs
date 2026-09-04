namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 定义内存快照生命周期允许的单向状态转移。
/// </summary>
public static class MemorySnapshotTransitionRules
{
    /// <summary>
    /// 判断快照能否从指定状态转移到目标状态。
    /// </summary>
    /// <param name="from">当前状态。</param>
    /// <param name="to">候选下一状态。</param>
    /// <returns>转移被允许时返回 <see langword="true"/>。</returns>
    public static bool CanMove(MemorySnapshotState from, MemorySnapshotState to) =>
        from switch
        {
            MemorySnapshotState.Pending => to is MemorySnapshotState.Capturing
                or MemorySnapshotState.Canceled
                or MemorySnapshotState.Failed,
            MemorySnapshotState.Capturing => to is MemorySnapshotState.Analyzing
                or MemorySnapshotState.Canceled
                or MemorySnapshotState.Failed,
            MemorySnapshotState.Analyzing => to is MemorySnapshotState.Ready
                or MemorySnapshotState.Canceled
                or MemorySnapshotState.Failed,
            MemorySnapshotState.Failed => to is MemorySnapshotState.Analyzing
                or MemorySnapshotState.Canceled,
            _ => false
        };
}
