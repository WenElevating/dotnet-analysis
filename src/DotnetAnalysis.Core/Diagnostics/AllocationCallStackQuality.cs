namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 描述分配热点中调用栈信息的可用程度。
/// </summary>
public enum AllocationCallStackQuality
{
    /// <summary>
    /// 每条已采样分配均已关联可用调用栈。
    /// </summary>
    Available,

    /// <summary>
    /// 部分采样分配缺少调用栈，或调用栈缓存已达到容量上限。
    /// </summary>
    Partial,

    /// <summary>
    /// 当前概要没有可用的调用栈信息。
    /// </summary>
    NotAvailable
}
