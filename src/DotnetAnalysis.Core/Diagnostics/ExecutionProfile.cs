namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示一个时间区间内的托管执行采样分析结果。
/// </summary>
public sealed record ExecutionProfile
{
    /// <summary>
    /// 创建执行采样分析结果。
    /// </summary>
    /// <param name="timeRange">分析覆盖的时间区间。</param>
    /// <param name="receivedSampleCount">收到的非负采样数。</param>
    /// <param name="lostEventCount">丢失的非负事件数。</param>
    /// <param name="hotspots">按方法聚合的热点。</param>
    /// <param name="callTreeRoots">调用树根节点。</param>
    /// <exception cref="ArgumentNullException">时间区间、热点集合或调用树根集合为 <see langword="null"/> 时引发。</exception>
    /// <exception cref="ArgumentOutOfRangeException">采样数或丢失事件数为负数时引发。</exception>
    public ExecutionProfile(
        ExecutionTimeRange timeRange,
        long receivedSampleCount,
        long lostEventCount,
        IReadOnlyList<ExecutionHotspot> hotspots,
        IReadOnlyList<ExecutionCallTreeNode> callTreeRoots)
    {
        ArgumentNullException.ThrowIfNull(timeRange);
        ArgumentNullException.ThrowIfNull(hotspots);
        ArgumentNullException.ThrowIfNull(callTreeRoots);
        ValidateCount(receivedSampleCount, nameof(receivedSampleCount));
        ValidateCount(lostEventCount, nameof(lostEventCount));

        TimeRange = timeRange;
        ReceivedSampleCount = receivedSampleCount;
        LostEventCount = lostEventCount;
        Hotspots = hotspots.ToArray();
        CallTreeRoots = callTreeRoots.ToArray();
    }

    /// <summary>
    /// 分析覆盖的时间区间。
    /// </summary>
    public ExecutionTimeRange TimeRange { get; }

    /// <summary>
    /// 收到的采样数。
    /// </summary>
    public long ReceivedSampleCount { get; }

    /// <summary>
    /// 丢失的事件数。
    /// </summary>
    public long LostEventCount { get; }

    /// <summary>
    /// 按方法聚合的热点。
    /// </summary>
    public IReadOnlyList<ExecutionHotspot> Hotspots { get; }

    /// <summary>
    /// 调用树根节点。
    /// </summary>
    public IReadOnlyList<ExecutionCallTreeNode> CallTreeRoots { get; }

    private static void ValidateCount(long value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Count cannot be negative.");
        }
    }
}
