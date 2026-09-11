namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示两个快照按 <see cref="TypeIdentity"/> 聚合后的增长比较结果。
/// </summary>
public sealed record MemorySnapshotComparison
{
    /// <summary>
    /// 创建快照比较结果。
    /// </summary>
    /// <param name="baselineSnapshotId">基准快照标识。</param>
    /// <param name="candidateSnapshotId">候选快照标识。</param>
    /// <param name="types">按浅表大小增长降序排列的类型增长项。</param>
    public MemorySnapshotComparison(
        MemorySnapshotId baselineSnapshotId,
        MemorySnapshotId candidateSnapshotId,
        IReadOnlyList<MemoryTypeGrowth> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        BaselineSnapshotId = baselineSnapshotId;
        CandidateSnapshotId = candidateSnapshotId;
        Types = types.ToArray();
    }

    /// <summary>
    /// 基准快照标识。
    /// </summary>
    public MemorySnapshotId BaselineSnapshotId { get; }

    /// <summary>
    /// 候选快照标识。
    /// </summary>
    public MemorySnapshotId CandidateSnapshotId { get; }

    /// <summary>
    /// 按类型聚合的增长项。
    /// </summary>
    public IReadOnlyList<MemoryTypeGrowth> Types { get; }
}
