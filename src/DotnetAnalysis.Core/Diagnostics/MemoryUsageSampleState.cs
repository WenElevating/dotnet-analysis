namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 描述内存使用样本数值的可用性。
/// </summary>
public enum MemoryUsageSampleState
{
    /// <summary>
    /// 托管堆和进程内存均已成功测量。
    /// </summary>
    Measured,
    /// <summary>
    /// 当前无法取得完整且一致的测量值。
    /// </summary>
    Unavailable,
    /// <summary>
    /// 目标会话已结束，不会再产生样本。
    /// </summary>
    SessionEnded
}
