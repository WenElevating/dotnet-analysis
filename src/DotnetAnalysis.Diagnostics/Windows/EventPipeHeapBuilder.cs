using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 在一次 EventPipe 消费期间构建供 .gcdump 写入和查询使用的堆图。
/// </summary>
internal sealed class EventPipeHeapBuilder
{
    private readonly Dictionary<ulong, TypeIdentity> _typeNames = [];
    private readonly List<HeapGraphNode> _nodes = [];
    private readonly List<ulong> _edgeTargets = [];
    private readonly List<ulong> _roots = [];
    private readonly Dictionary<TypeIdentity, (long Count, long Size)> _aggregate = [];
    private readonly long _startedAtStopwatchTicks = Stopwatch.GetTimestamp();
    private long _gcStartCount;
    private long _gcStopCount;
    private long _nodeBatchCount;
    private long _nodeCount;
    private long _declaredEdgeCount;
    private long _edgeBatchCount;
    private long _edgeCount;
    private long _rootEdgeBatchCount;
    private long _rootEdgeCount;
    private long _firstNodeUtcTicks;
    private long _lastNodeUtcTicks;
    private long _gcStartObservedStopwatchTicks;
    private long _gcStopObservedStopwatchTicks;
    private long _firstNodeObservedStopwatchTicks;
    private long _lastNodeObservedStopwatchTicks;
    private long _lastEdgeObservedStopwatchTicks;
    private long _lastRootEdgeObservedStopwatchTicks;
    private int _hasHeapData;

    /// <summary>
    /// 指示是否已读取到至少一个可用堆节点。
    /// </summary>
    public bool HasHeapData => Volatile.Read(ref _hasHeapData) != 0;

    /// <summary>
    /// 指示所有堆节点声明的边槽位是否都已从 EventPipe 流中到达。
    /// </summary>
    public bool HasReceivedAllDeclaredEdges =>
        HasHeapData
        && Volatile.Read(ref _edgeCount) >= Volatile.Read(ref _declaredEdgeCount);

    /// <summary>
    /// 返回已按 EventPipe 节点事件顺序收集的紧凑图节点，供捕获写入器直接编码。
    /// </summary>
    internal IReadOnlyList<HeapGraphNode> Nodes => _nodes;

    /// <summary>
    /// 返回与 <see cref="Nodes"/> 声明的边槽位顺序一致的目标地址序列。
    /// </summary>
    internal IReadOnlyList<ulong> EdgeTargets => _edgeTargets;

    /// <summary>
    /// 返回 EventPipe 根边中引用的对象地址。
    /// </summary>
    internal IReadOnlyList<ulong> Roots => _roots;

    /// <summary>
    /// 将堆图事件处理器附加到指定 EventPipe 事件源。
    /// </summary>
    public void Attach(EventPipeEventSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.Clr.GCStart += _ => RecordGcStart();
        source.Clr.GCStop += _ => RecordGcStop();
        source.Clr.TypeBulkType += data =>
        {
            for (var index = 0; index < data.Count; index++)
            {
                var value = data.Values(index);
                var name = string.IsNullOrWhiteSpace(value.TypeName)
                    ? $"Type(0x{value.TypeNameID:x})"
                    : value.TypeName;
                _typeNames[value.TypeID] = new TypeIdentity(name, null);
            }
        };
        source.Clr.GCBulkNode += data =>
        {
            var timestampTicks = data.TimeStamp.ToUniversalTime().Ticks;
            var observedAtStopwatchTicks = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _nodeBatchCount);
            Interlocked.CompareExchange(ref _firstNodeUtcTicks, timestampTicks, 0);
            Interlocked.Exchange(ref _lastNodeUtcTicks, timestampTicks);
            Interlocked.CompareExchange(ref _firstNodeObservedStopwatchTicks, observedAtStopwatchTicks, 0);
            Interlocked.Exchange(ref _lastNodeObservedStopwatchTicks, observedAtStopwatchTicks);
            Interlocked.Add(ref _nodeCount, data.Count);
            long declaredEdges = 0;
            for (var index = 0; index < data.Count; index++)
            {
                var value = data.Values(index);
                if (value.Size > long.MaxValue)
                {
                    continue;
                }

                Volatile.Write(ref _hasHeapData, 1);
                var type = _typeNames.TryGetValue(value.TypeID, out var knownType)
                    ? knownType
                    : new TypeIdentity($"Type(0x{value.TypeID:x})", null);
                var sizeBytes = (long)value.Size;
                _nodes.Add(new HeapGraphNode(value.Address, type, sizeBytes, value.EdgeCount));
                declaredEdges = checked(declaredEdges + Math.Max(value.EdgeCount, 0));
                _aggregate.TryGetValue(type, out var current);
                _aggregate[type] = (current.Count + 1, checked(current.Size + sizeBytes));
            }

            Interlocked.Add(ref _declaredEdgeCount, declaredEdges);
        };
        source.Clr.GCBulkEdge += data =>
        {
            Interlocked.Exchange(ref _lastEdgeObservedStopwatchTicks, Stopwatch.GetTimestamp());
            Interlocked.Increment(ref _edgeBatchCount);
            Interlocked.Add(ref _edgeCount, data.Count);
            for (var index = 0; index < data.Count; index++)
            {
                _edgeTargets.Add(data.Values(index).Target);
            }
        };
        source.Clr.GCBulkRootEdge += data =>
        {
            Interlocked.Exchange(ref _lastRootEdgeObservedStopwatchTicks, Stopwatch.GetTimestamp());
            Interlocked.Increment(ref _rootEdgeBatchCount);
            Interlocked.Add(ref _rootEdgeCount, data.Count);
            for (var index = 0; index < data.Count; index++)
            {
                var address = data.Values(index).RootedNodeAddress;
                if (address != 0)
                {
                    _roots.Add(address);
                }
            }
        };
    }

    /// <summary>
    /// 返回当前 EventPipe 流的低成本进度快照，以便区分运行时未完成和本地消费者落后的捕获失败。
    /// </summary>
    public EventPipeHeapBuilderDiagnostics GetDiagnostics() => new(
        Volatile.Read(ref _gcStartCount),
        Volatile.Read(ref _gcStopCount),
        Volatile.Read(ref _nodeBatchCount),
        Volatile.Read(ref _nodeCount),
        Volatile.Read(ref _edgeBatchCount),
        Volatile.Read(ref _edgeCount),
        Volatile.Read(ref _rootEdgeBatchCount),
        Volatile.Read(ref _rootEdgeCount),
        ToUtcDateTimeOffset(Volatile.Read(ref _firstNodeUtcTicks)),
        ToUtcDateTimeOffset(Volatile.Read(ref _lastNodeUtcTicks)),
        ToElapsedMilliseconds(Volatile.Read(ref _gcStartObservedStopwatchTicks)),
        ToElapsedMilliseconds(Volatile.Read(ref _gcStopObservedStopwatchTicks)),
        ToElapsedMilliseconds(Volatile.Read(ref _firstNodeObservedStopwatchTicks)),
        ToElapsedMilliseconds(Volatile.Read(ref _lastNodeObservedStopwatchTicks)),
        ToElapsedMilliseconds(Volatile.Read(ref _lastEdgeObservedStopwatchTicks)),
        ToElapsedMilliseconds(Volatile.Read(ref _lastRootEdgeObservedStopwatchTicks)));

    /// <summary>
    /// 完成一次 EventPipe 消费后生成统一堆图。
    /// </summary>
    public GCDumpSnapshotReader.HeapData Build()
    {
        if (!HasHeapData)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.CaptureFailed,
                "The gcdump did not contain heap object events.");
        }

        var summaries = _aggregate
            .Select(entry => new MemoryTypeSummary(entry.Key, entry.Value.Count, entry.Value.Size))
            .OrderByDescending(summary => summary.TotalSizeBytes)
            .ThenBy(summary => summary.Type.TypeName, StringComparer.Ordinal)
            .ToArray();
        return new GCDumpSnapshotReader.HeapData(
            new ReadOnlyCollection<MemoryTypeSummary>(summaries),
            new ReadOnlyCollection<MemoryObjectInfo>(_nodes
                .Select(node => new MemoryObjectInfo(node.Address, node.Type, node.SizeBytes))
                .ToList()),
            BuildEdges(),
            _roots.Distinct().ToArray());
    }

    private Dictionary<ulong, IReadOnlyList<ulong>> BuildEdges()
    {
        var edges = new Dictionary<ulong, IReadOnlyList<ulong>>();
        var edgeIndex = 0;
        foreach (var node in _nodes)
        {
            var count = node.EdgeCount > 0
                ? Math.Min(node.EdgeCount, _edgeTargets.Count - edgeIndex)
                : 0;
            if (count > 0)
            {
                edges[node.Address] = _edgeTargets
                    .Skip(edgeIndex)
                    .Take((int)count)
                    .Where(address => address != 0)
                    .ToArray();
                edgeIndex += (int)count;
            }
        }

        return edges;
    }

    private static DateTimeOffset? ToUtcDateTimeOffset(long ticks) =>
        ticks == 0 ? null : new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));

    private void RecordGcStart()
    {
        Interlocked.Increment(ref _gcStartCount);
        Interlocked.Exchange(ref _gcStartObservedStopwatchTicks, Stopwatch.GetTimestamp());
    }

    private void RecordGcStop()
    {
        Interlocked.Increment(ref _gcStopCount);
        Interlocked.Exchange(ref _gcStopObservedStopwatchTicks, Stopwatch.GetTimestamp());
    }

    private double? ToElapsedMilliseconds(long observedStopwatchTicks) =>
        observedStopwatchTicks == 0
            ? null
            : Stopwatch.GetElapsedTime(_startedAtStopwatchTicks, observedStopwatchTicks).TotalMilliseconds;

}

/// <summary>
/// 表示 EventPipe 堆图中的一个紧凑对象行及其后续边槽位数量。
/// </summary>
internal readonly record struct HeapGraphNode(
    ulong Address,
    TypeIdentity Type,
    long SizeBytes,
    long EdgeCount);

/// <summary>
/// 描述 EventPipe 堆快照流在超时或失败时已被本地消费者处理的进度。
/// </summary>
internal sealed record EventPipeHeapBuilderDiagnostics(
    long GcStartCount,
    long GcStopCount,
    long NodeBatchCount,
    long NodeCount,
    long EdgeBatchCount,
    long EdgeCount,
    long RootEdgeBatchCount,
    long RootEdgeCount,
    DateTimeOffset? FirstNodeUtc,
    DateTimeOffset? LastNodeUtc,
    double? GcStartObservedMilliseconds,
    double? GcStopObservedMilliseconds,
    double? FirstNodeObservedMilliseconds,
    double? LastNodeObservedMilliseconds,
    double? LastEdgeObservedMilliseconds,
    double? LastRootEdgeObservedMilliseconds);
