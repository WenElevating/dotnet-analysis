namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示执行采样的 UTC 时间区间。
/// </summary>
public sealed record ExecutionTimeRange
{
    /// <summary>
    /// 创建执行采样时间区间。
    /// </summary>
    /// <param name="startAtUtc">区间开始时间。</param>
    /// <param name="endAtUtc">区间结束时间。</param>
    /// <exception cref="ArgumentOutOfRangeException">结束时间不晚于开始时间时引发。</exception>
    public ExecutionTimeRange(DateTimeOffset startAtUtc, DateTimeOffset endAtUtc)
    {
        StartAtUtc = startAtUtc.ToUniversalTime();
        EndAtUtc = endAtUtc.ToUniversalTime();
        if (EndAtUtc <= StartAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(endAtUtc), endAtUtc, "End time must be after start time.");
        }
    }

    /// <summary>
    /// 区间开始 UTC 时间。
    /// </summary>
    public DateTimeOffset StartAtUtc { get; }

    /// <summary>
    /// 区间结束 UTC 时间。
    /// </summary>
    public DateTimeOffset EndAtUtc { get; }
}
