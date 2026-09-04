using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 线程安全地聚合分配采样并生成不可变概要。
/// </summary>
public sealed class AllocationProfileBuilder
{
    private readonly object _gate = new();
    private DateTimeOffset _startedAtUtc;
    private readonly Dictionary<(TypeIdentity Type, string Frames), (long Bytes, IReadOnlyList<CallStackFrame> Frames)> _entries = [];
    private bool _interrupted;
    private bool _hasCallStacks;
    private bool _hasMissingCallStacks;

    /// <summary>
    /// 以指定开始时间创建一个线程安全的分配采样区间聚合器。
    /// </summary>
    /// <param name="startedAtUtc">当前采样区间的开始时间。</param>
    public AllocationProfileBuilder(DateTimeOffset startedAtUtc)
    {
        _startedAtUtc = startedAtUtc;
    }

    /// <summary>
    /// 合并一个类型与调用栈组合观察到的分配字节数。
    /// </summary>
    /// <param name="type">被分配对象的类型。</param>
    /// <param name="frames">标识分配位置的调用栈帧。</param>
    /// <param name="observedAllocatedBytes">本次观察到的非负分配字节数。</param>
    public void Add(TypeIdentity type, IReadOnlyList<CallStackFrame> frames, long observedAllocatedBytes)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfNegative(observedAllocatedBytes);

        lock (_gate)
        {
            var normalizedFrames = frames.ToArray();
            var key = (type, string.Join("|", normalizedFrames.Select(frame => frame.Name)));
            if (_entries.TryGetValue(key, out var current))
            {
                _entries[key] = (checked(current.Bytes + observedAllocatedBytes), current.Frames);
            }
            else
            {
                _entries[key] = (observedAllocatedBytes, normalizedFrames);
            }

            _hasCallStacks = true;
        }
    }

    /// <summary>
    /// 将当前区间标记为不连续，表示采样流曾中断或不完整。
    /// </summary>
    /// <param name="observedAtUtc">中断被观察到的时间；目前仅保留为调用语义。</param>
    public void MarkInterrupted(DateTimeOffset observedAtUtc)
    {
        lock (_gate)
        {
            _interrupted = true;
        }
    }

    /// <summary>
    /// 标记至少一条分配样本没有可解析调用栈，且不伪造替代帧。
    /// </summary>
    public void MarkCallStackUnavailable()
    {
        lock (_gate)
        {
            _hasMissingCallStacks = true;
        }
    }

    /// <summary>
    /// 冻结当前区间并生成按观察分配量排序的分配概要。
    /// </summary>
    /// <param name="capturedAtUtc">快照捕获完成时间；早于开始时间时会被钳制。</param>
    /// <returns>包含热点和数据完整性标记的不可变分配概要。</returns>
    public AllocationProfile Seal(DateTimeOffset capturedAtUtc)
    {
        lock (_gate)
        {
            return new AllocationProfile(
                _startedAtUtc,
                capturedAtUtc < _startedAtUtc ? _startedAtUtc : capturedAtUtc,
                _entries
                    .Select(entry => new AllocationHotspot(entry.Key.Type, entry.Value.Bytes, entry.Value.Frames))
                    .OrderByDescending(hotspot => hotspot.ObservedAllocatedBytes)
                    .ToArray(),
                _interrupted ? AllocationProfileDataQuality.Interrupted : AllocationProfileDataQuality.Continuous,
                _hasCallStacks
                    ? _hasMissingCallStacks
                        ? AllocationCallStackQuality.Partial
                        : AllocationCallStackQuality.Available
                    : AllocationCallStackQuality.NotAvailable);
        }
    }

    /// <summary>
    /// 清空当前聚合结果并开始下一个独立采样区间。
    /// </summary>
    /// <param name="startedAtUtc">下一个区间的开始时间。</param>
    public void BeginNextInterval(DateTimeOffset startedAtUtc)
    {
        lock (_gate)
        {
            _startedAtUtc = startedAtUtc;
            _entries.Clear();
            _interrupted = false;
            _hasCallStacks = false;
            _hasMissingCallStacks = false;
        }
    }
}
