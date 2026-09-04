namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 描述分配采样结果是否连续、被中断或不可用。
/// </summary>
public enum AllocationProfileDataQuality
{
    /// <summary>
    /// 采样区间连续完成。
    /// </summary>
    Continuous,
    /// <summary>
    /// 采样流曾中断，结果可能不完整。
    /// </summary>
    Interrupted,
    /// <summary>
    /// 没有可用分配采样数据。
    /// </summary>
    NotAvailable
}
