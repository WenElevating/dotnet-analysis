namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示同一类型在两个快照之间的对象数和浅表大小变化。
/// </summary>
public sealed record MemoryTypeGrowth
{
    /// <summary>
    /// 创建一个按类型聚合的快照增长项。
    /// </summary>
    /// <param name="type">聚合类型身份。</param>
    /// <param name="baselineObjectCount">基准快照中的对象数。</param>
    /// <param name="candidateObjectCount">候选快照中的对象数。</param>
    /// <param name="baselineShallowSizeBytes">基准快照中的浅表大小。</param>
    /// <param name="candidateShallowSizeBytes">候选快照中的浅表大小。</param>
    public MemoryTypeGrowth(
        TypeIdentity type,
        long baselineObjectCount,
        long candidateObjectCount,
        long baselineShallowSizeBytes,
        long candidateShallowSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentOutOfRangeException.ThrowIfNegative(baselineObjectCount);
        ArgumentOutOfRangeException.ThrowIfNegative(candidateObjectCount);
        ArgumentOutOfRangeException.ThrowIfNegative(baselineShallowSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(candidateShallowSizeBytes);

        Type = type;
        BaselineObjectCount = baselineObjectCount;
        CandidateObjectCount = candidateObjectCount;
        BaselineShallowSizeBytes = baselineShallowSizeBytes;
        CandidateShallowSizeBytes = candidateShallowSizeBytes;
    }

    /// <summary>
    /// 聚合类型身份。
    /// </summary>
    public TypeIdentity Type { get; }

    /// <summary>
    /// 基准快照中的对象数量。
    /// </summary>
    public long BaselineObjectCount { get; }

    /// <summary>
    /// 候选快照中的对象数量。
    /// </summary>
    public long CandidateObjectCount { get; }

    /// <summary>
    /// 基准快照中的浅表大小。
    /// </summary>
    public long BaselineShallowSizeBytes { get; }

    /// <summary>
    /// 候选快照中的浅表大小。
    /// </summary>
    public long CandidateShallowSizeBytes { get; }

    /// <summary>
    /// 对象数量的有符号变化值。
    /// </summary>
    public long ObjectCountGrowth => checked(CandidateObjectCount - BaselineObjectCount);

    /// <summary>
    /// 浅表大小的有符号变化值。
    /// </summary>
    public long ShallowSizeGrowthBytes => checked(CandidateShallowSizeBytes - BaselineShallowSizeBytes);
}
