namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示按方法聚合的执行采样热点。
/// </summary>
public sealed record ExecutionHotspot
{
    /// <summary>
    /// 创建执行采样热点。
    /// </summary>
    /// <param name="frame">热点对应的调用栈帧。</param>
    /// <param name="inclusiveSampleCount">包含子调用的非负采样数。</param>
    /// <param name="exclusiveSampleCount">仅该帧的非负采样数。</param>
    /// <exception cref="ArgumentNullException">调用栈帧为 <see langword="null"/> 时引发。</exception>
    /// <exception cref="ArgumentOutOfRangeException">任一采样数为负数时引发。</exception>
    public ExecutionHotspot(ExecutionFrame frame, long inclusiveSampleCount, long exclusiveSampleCount)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateSampleCount(inclusiveSampleCount, nameof(inclusiveSampleCount));
        ValidateSampleCount(exclusiveSampleCount, nameof(exclusiveSampleCount));

        Frame = frame;
        InclusiveSampleCount = inclusiveSampleCount;
        ExclusiveSampleCount = exclusiveSampleCount;
    }

    /// <summary>
    /// 热点对应的调用栈帧。
    /// </summary>
    public ExecutionFrame Frame { get; }

    /// <summary>
    /// 包含子调用的采样数。
    /// </summary>
    public long InclusiveSampleCount { get; }

    /// <summary>
    /// 仅该帧的采样数。
    /// </summary>
    public long ExclusiveSampleCount { get; }

    private static void ValidateSampleCount(long sampleCount, string parameterName)
    {
        if (sampleCount < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, sampleCount, "Sample count cannot be negative.");
        }
    }
}
