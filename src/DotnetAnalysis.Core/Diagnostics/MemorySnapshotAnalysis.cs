namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 汇总快照的类型统计、对象图分析和分配概要。
/// </summary>
public sealed record MemorySnapshotAnalysis
{
    /// <summary>
    /// 允许一次完整枚举对象的最大快照对象数量。
    /// </summary>
    public const long FullObjectEnumerationLimit = 100_000;

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
        : this(snapshot, types, allocationProfile, DetermineObjectAccessMode(types?.Sum(summary => summary.ObjectCount) ?? 0))
    {
    }

    /// <summary>
    /// 创建带有明确对象访问方式的快照分析结果。
    /// </summary>
    /// <param name="snapshot">分析后的快照描述。</param>
    /// <param name="types">按类型聚合的对象统计。</param>
    /// <param name="allocationProfile">关联的分配概要。</param>
    /// <param name="objectAccessMode">对象实例的读取方式。</param>
    public MemorySnapshotAnalysis(
        MemorySnapshot snapshot,
        IReadOnlyList<MemoryTypeSummary> types,
        AllocationProfile allocationProfile,
        MemorySnapshotObjectAccessMode objectAccessMode)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(allocationProfile);

        Snapshot = snapshot;
        Types = types.ToArray();
        AllocationProfile = allocationProfile;
        ObjectAccessMode = objectAccessMode;
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

    /// <summary>
    /// 快照对象实例的读取方式。
    /// </summary>
    public MemorySnapshotObjectAccessMode ObjectAccessMode { get; }

    /// <summary>
    /// 根据快照中的对象总数量确定对象实例的读取方式。
    /// </summary>
    /// <param name="objectCount">快照中的对象总数量。</param>
    /// <returns>小于 100,000 个对象时为 <see cref="MemorySnapshotObjectAccessMode.Full"/>；否则为 <see cref="MemorySnapshotObjectAccessMode.Paged"/>。</returns>
    /// <exception cref="ArgumentOutOfRangeException">对象数量为负数时引发。</exception>
    public static MemorySnapshotObjectAccessMode DetermineObjectAccessMode(long objectCount)
    {
        if (objectCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(objectCount), objectCount, "Object count cannot be negative.");
        }

        return objectCount < FullObjectEnumerationLimit
            ? MemorySnapshotObjectAccessMode.Full
            : MemorySnapshotObjectAccessMode.Paged;
    }
}
