using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 在单个分配采样区间内去重并限制调用栈常驻数量。
/// </summary>
internal sealed class AllocationCallStackCache
{
    private readonly object _syncRoot = new();
    private readonly int _capacity;
    private readonly Dictionary<string, IReadOnlyList<CallStackFrame>> _stacks = [];

    /// <summary>
    /// 创建指定最大调用栈数的区间缓存。
    /// </summary>
    public AllocationCallStackCache(int capacity = 4_096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>
    /// 返回已缓存或新加入的调用栈；容量已满且栈未出现过时返回 false。
    /// </summary>
    public bool TryGetOrAdd(IReadOnlyList<CallStackFrame> frames, out IReadOnlyList<CallStackFrame> cached)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var snapshot = frames.ToArray();
        var key = string.Join(
            "\u001f",
            snapshot.Select(frame => $"{frame.Name}\u001e{frame.ModuleName}\u001e{frame.LineNumber}"));
        lock (_syncRoot)
        {
            if (_stacks.TryGetValue(key, out cached!))
            {
                return true;
            }

            if (_stacks.Count >= _capacity)
            {
                cached = Array.Empty<CallStackFrame>();
                return false;
            }

            cached = snapshot;
            _stacks.Add(key, cached);
            return true;
        }
    }
}
