namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 汇总快照的类型统计、对象图分析和分配概要。
/// </summary>
public sealed record MemorySnapshotAnalysis
{
    /// <summary>
    /// 创建快照分析结果。
    /// </summary>
    /// <param name="snapshot">分析后的快照描述。</param>
    /// <param name="types">按类型聚合的对象统计。</param>
    /// <param name="allocationProfile">关联的分配概要。</param>
    public MemorySnapshotAnalysis(
        MemorySnapshot snapshot,
        IReadOnlyList<MemoryTypeSummary> types,
        AllocationProfile allocationProfile)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(allocationProfile);

        Snapshot = snapshot;
        Types = types.ToArray();
        AllocationProfile = allocationProfile;
    }

    /// <summary>
    /// 分析后的快照描述。
    /// </summary>
    public MemorySnapshot Snapshot { get; }

    /// <summary>
    /// 按类型聚合的对象统计。
    /// </summary>
    public IReadOnlyList<MemoryTypeSummary> Types { get; }

    /// <summary>
    /// 关联的分配概要。
    /// </summary>
    public AllocationProfile AllocationProfile { get; }
}
