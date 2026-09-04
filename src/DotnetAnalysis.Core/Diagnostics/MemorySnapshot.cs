namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 描述一次托管堆快照及其捕获、分析生命周期。
/// </summary>
public sealed record MemorySnapshot
{
    /// <summary>
    /// 创建快照描述。
    /// </summary>
    /// <param name="id">快照标识。</param>
    /// <param name="origin">快照来源。</param>
    /// <param name="requestedAtUtc">请求时间。</param>
    /// <param name="captureStartedAtUtc">实际开始捕获时间。</param>
    /// <param name="capturedAtUtc">捕获完成时间。</param>
    /// <param name="state">初始生命周期状态。</param>
    public MemorySnapshot(
        MemorySnapshotId id,
        MemorySnapshotOrigin origin,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset? captureStartedAtUtc,
        DateTimeOffset? capturedAtUtc,
        MemorySnapshotState state)
    {
        Id = id;
        Origin = origin;
        RequestedAtUtc = requestedAtUtc;
        CaptureStartedAtUtc = captureStartedAtUtc;
        CapturedAtUtc = capturedAtUtc;
        State = state;
    }

    /// <summary>
    /// 快照标识。
    /// </summary>
    public MemorySnapshotId Id { get; }

    /// <summary>
    /// 快照来源类型。
    /// </summary>
    public MemorySnapshotOrigin Origin { get; }

    /// <summary>
    /// 用户请求快照的时间。
    /// </summary>
    public DateTimeOffset RequestedAtUtc { get; }

    /// <summary>
    /// 开始捕获的时间；导入快照为空。
    /// </summary>
    public DateTimeOffset? CaptureStartedAtUtc { get; }

    /// <summary>
    /// 捕获完成或文件写入时间；尚未完成时为空。
    /// </summary>
    public DateTimeOffset? CapturedAtUtc { get; }

    /// <summary>
    /// 当前生命周期状态。
    /// </summary>
    public MemorySnapshotState State { get; private init; }

    /// <summary>
    /// 按核心状态转移规则创建下一个状态的快照副本。
    /// </summary>
    internal MemorySnapshot MoveTo(MemorySnapshotState nextState)
    {
        if (!MemorySnapshotTransitionRules.CanMove(State, nextState))
        {
            throw new InvalidOperationException($"Cannot move memory snapshot from {State} to {nextState}.");
        }

        return this with { State = nextState };
    }
}
