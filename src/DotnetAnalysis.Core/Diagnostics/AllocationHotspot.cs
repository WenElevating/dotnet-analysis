namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示按对象类型和调用栈聚合的分配热点。
/// </summary>
public sealed record AllocationHotspot
{
    /// <summary>
    /// 创建一个分配热点。
    /// </summary>
    /// <param name="type">分配对象的类型。</param>
    /// <param name="observedAllocatedBytes">采样期间观察到的分配字节数。</param>
    /// <param name="frames">关联的调用栈帧。</param>
    public AllocationHotspot(
        TypeIdentity type,
        long observedAllocatedBytes,
        IReadOnlyList<CallStackFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(frames);
        if (observedAllocatedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedAllocatedBytes),
                observedAllocatedBytes,
                "Observed allocated bytes cannot be negative.");
        }

        Type = type;
        ObservedAllocatedBytes = observedAllocatedBytes;
        Frames = frames.ToArray();
    }

    /// <summary>
    /// 热点对象类型。
    /// </summary>
    public TypeIdentity Type { get; }

    /// <summary>
    /// 观察到的分配字节数。
    /// </summary>
    public long ObservedAllocatedBytes { get; }

    /// <summary>
    /// 用于定位分配来源的调用栈。
    /// </summary>
    public IReadOnlyList<CallStackFrame> Frames { get; }
}
