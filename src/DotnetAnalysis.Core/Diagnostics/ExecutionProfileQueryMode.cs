namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 指定执行采样分析查询所使用的数据读取方式。
/// </summary>
public enum ExecutionProfileQueryMode
{
    /// <summary>
    /// 始终扫描固定读取边界内的原始采样段，用作准确性基准和排障路径。
    /// </summary>
    FullScan,

    /// <summary>
    /// 合并完整封存段的增量摘要，并扫描边界或活动段以减少查询成本。
    /// </summary>
    Incremental
}
