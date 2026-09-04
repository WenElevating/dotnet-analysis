namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示一次进程私有工作集和托管堆大小采样。
/// </summary>
public sealed record MemoryUsageSample
{
    /// <summary>
    /// 创建内存使用样本，并验证数值与状态组合一致。
    /// </summary>
    /// <param name="observedAtUtc">采样观察时间。</param>
    /// <param name="managedHeapBytes">托管堆大小；不可用时为空。</param>
    /// <param name="processMemoryBytes">进程私有工作集大小；不可用时为空。</param>
    /// <param name="state">样本可用性或会话终止状态。</param>
    public MemoryUsageSample(
        DateTimeOffset observedAtUtc,
        long? managedHeapBytes,
        long? processMemoryBytes,
        MemoryUsageSampleState state)
    {
        if (managedHeapBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(managedHeapBytes), managedHeapBytes, "Byte values cannot be negative.");
        }

        if (processMemoryBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processMemoryBytes), processMemoryBytes, "Byte values cannot be negative.");
        }

        switch (state)
        {
            case MemoryUsageSampleState.Measured when managedHeapBytes is null || processMemoryBytes is null:
                throw new ArgumentException("Measured samples require both byte values.", nameof(state));
            case MemoryUsageSampleState.Unavailable or MemoryUsageSampleState.SessionEnded
                when managedHeapBytes is not null || processMemoryBytes is not null:
                throw new ArgumentException("Unavailable and session-ended samples must not contain byte values.", nameof(state));
        }

        ObservedAtUtc = observedAtUtc;
        ManagedHeapBytes = managedHeapBytes;
        ProcessMemoryBytes = processMemoryBytes;
        State = state;
    }

    /// <summary>
    /// 采样观察时间。
    /// </summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>
    /// 托管堆大小（字节）；不可用时为空。
    /// </summary>
    public long? ManagedHeapBytes { get; }

    /// <summary>
    /// 进程私有工作集大小（字节）；不可用时为空。
    /// </summary>
    public long? ProcessMemoryBytes { get; }

    /// <summary>
    /// 样本状态。
    /// </summary>
    public MemoryUsageSampleState State { get; }
}
