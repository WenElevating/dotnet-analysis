namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 内存快照在捕获和分析过程中的生命周期状态。
/// </summary>
public enum MemorySnapshotState
{
    /// <summary>
    /// 已创建但尚未开始捕获。
    /// </summary>
    Pending,
    /// <summary>
    /// 正在从目标进程捕获数据。
    /// </summary>
    Capturing,
    /// <summary>
    /// 已具备数据，正在分析。
    /// </summary>
    Analyzing,
    /// <summary>
    /// 分析完成，可供查询。
    /// </summary>
    Ready,
    /// <summary>
    /// 捕获或分析失败。
    /// </summary>
    Failed,
    /// <summary>
    /// 操作被取消。
    /// </summary>
    Canceled
}
