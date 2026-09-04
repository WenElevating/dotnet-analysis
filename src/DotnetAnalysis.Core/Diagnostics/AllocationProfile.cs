namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示一个采样时间区间内的分配热点及数据完整性。
/// </summary>
public sealed record AllocationProfile
{
    /// <summary>
    /// 创建分配概要。
    /// </summary>
    /// <param name="startedAtUtc">采样开始时间。</param>
    /// <param name="endedAtUtc">采样结束时间。</param>
    /// <param name="hotspots">聚合后的分配热点。</param>
    /// <param name="dataQuality">采样数据完整性。</param>
    public AllocationProfile(
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc,
        IReadOnlyList<AllocationHotspot> hotspots,
        AllocationProfileDataQuality dataQuality)
    {
        ArgumentNullException.ThrowIfNull(hotspots);
        if (endedAtUtc < startedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(endedAtUtc), endedAtUtc, "End time cannot precede start time.");
        }

        var hotspotSnapshot = hotspots.ToArray();
        if (dataQuality is AllocationProfileDataQuality.NotAvailable && hotspotSnapshot.Length != 0)
        {
            throw new ArgumentException("Unavailable allocation profiles cannot contain hotspots.", nameof(hotspots));
        }

        StartedAtUtc = startedAtUtc;
        EndedAtUtc = endedAtUtc;
        Hotspots = hotspotSnapshot;
        DataQuality = dataQuality;
    }

    /// <summary>
    /// 采样开始时间。
    /// </summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>
    /// 采样结束时间。
    /// </summary>
    public DateTimeOffset EndedAtUtc { get; }

    /// <summary>
    /// 按分配量排序的热点列表。
    /// </summary>
    public IReadOnlyList<AllocationHotspot> Hotspots { get; }

    /// <summary>
    /// 采样数据完整性标记。
    /// </summary>
    public AllocationProfileDataQuality DataQuality { get; }

    /// <summary>
    /// 创建没有可用分配数据的概要。
    /// </summary>
    /// <param name="startedAtUtc">逻辑区间开始时间。</param>
    /// <param name="endedAtUtc">逻辑区间结束时间。</param>
    /// <returns>热点为空且质量为不可用的概要。</returns>
    public static AllocationProfile NotAvailable(DateTimeOffset startedAtUtc, DateTimeOffset endedAtUtc) =>
        new(startedAtUtc, endedAtUtc, Array.Empty<AllocationHotspot>(), AllocationProfileDataQuality.NotAvailable);
}
