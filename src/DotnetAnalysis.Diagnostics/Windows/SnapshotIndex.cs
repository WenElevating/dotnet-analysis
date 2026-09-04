using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 使用紧凑对象行保存一个快照的类型、地址和可选引用边。
/// </summary>
internal sealed class SnapshotIndex
{
    private readonly TypeIdentity[] _types;
    private readonly IndexedObjectRow[] _objects;
    private readonly Dictionary<TypeIdentity, int[]> _objectIndexesByType;
    private readonly Dictionary<ulong, int> _objectIndexesByAddress;
    private readonly Dictionary<int, int[]> _edges;
    private readonly int[] _roots;
    private readonly object _reverseEdgesSync = new();
    private readonly Action? _reverseIndexBuildStarting;
    private Dictionary<int, int[]>? _reverseEdges;
    private int _reverseIndexBuildCount;

    /// <summary>
    /// 从对象行以及可选的地址引用边创建快照索引。
    /// </summary>
    public SnapshotIndex(
        IReadOnlyList<ObjectRow> objects,
        IReadOnlyDictionary<ulong, IReadOnlyList<ulong>>? edges = null,
        IReadOnlyList<ulong>? roots = null,
        Action? reverseIndexBuildStarting = null)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var typeIndexes = new Dictionary<TypeIdentity, int>();
        var types = new List<TypeIdentity>();
        _objects = new IndexedObjectRow[objects.Count];
        _objectIndexesByAddress = new Dictionary<ulong, int>(objects.Count);
        var groupedIndexes = new Dictionary<TypeIdentity, List<int>>();

        for (var index = 0; index < objects.Count; index++)
        {
            var row = objects[index];
            ArgumentNullException.ThrowIfNull(row.Type);
            if (row.SizeBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(objects), "Object size cannot be negative.");
            }

            if (!typeIndexes.TryGetValue(row.Type, out var typeIndex))
            {
                typeIndex = types.Count;
                typeIndexes.Add(row.Type, typeIndex);
                types.Add(row.Type);
            }

            _objects[index] = new IndexedObjectRow(row.Address, typeIndex, row.SizeBytes);
            _objectIndexesByAddress.TryAdd(row.Address, index);
            if (!groupedIndexes.TryGetValue(row.Type, out var indexes))
            {
                indexes = [];
                groupedIndexes.Add(row.Type, indexes);
            }

            indexes.Add(index);
        }

        _types = types.ToArray();
        _objectIndexesByType = groupedIndexes.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray());
        _edges = BuildEdges(edges);
        _reverseIndexBuildStarting = reverseIndexBuildStarting;
        _roots = (roots ?? [])
            .Select(address => _objectIndexesByAddress.TryGetValue(address, out var index) ? index : -1)
            .Where(index => index >= 0)
            .Distinct()
            .ToArray();
        TypeSummaries = BuildTypeSummaries();
        ObjectAccessMode = MemorySnapshotAnalysis.DetermineObjectAccessMode(_objects.LongLength);
    }

    /// <summary>
    /// 从读取器的统一堆模型压缩为常驻查询索引。
    /// </summary>
    internal static SnapshotIndex FromHeap(GCDumpSnapshotReader.HeapData heap)
    {
        ArgumentNullException.ThrowIfNull(heap);
        return new SnapshotIndex(
            heap.Objects.Select(candidate => new ObjectRow(candidate.Address, candidate.Type, candidate.SizeBytes)).ToArray(),
            heap.Edges,
            heap.Roots);
    }

    /// <summary>
    /// 快照总对象数对应的对象读取方式。
    /// </summary>
    public MemorySnapshotObjectAccessMode ObjectAccessMode { get; }

    /// <summary>
    /// 按总大小排序的类型统计。
    /// </summary>
    public IReadOnlyList<MemoryTypeSummary> TypeSummaries { get; }

    /// <summary>
    /// 获取已完成的反向边索引构建次数，用于验证同一快照只构建一次。
    /// </summary>
    internal int ReverseIndexBuildCount => Volatile.Read(ref _reverseIndexBuildCount);

    /// <summary>
    /// 返回指定类型的所有对象；大型快照会稳定拒绝该调用。
    /// </summary>
    public IReadOnlyList<MemoryObjectInfo> GetObjects(TypeIdentity type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (ObjectAccessMode is MemorySnapshotObjectAccessMode.Paged)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.SnapshotTooLargeForFullEnumeration,
                "The snapshot is too large for full object enumeration. Use GetObjectsPageAsync instead.");
        }

        return !_objectIndexesByType.TryGetValue(type, out var indexes)
            ? []
            : indexes.Select(Project).ToArray();
    }

    /// <summary>
    /// 返回指定类型的请求对象页。
    /// </summary>
    public MemoryObjectPage GetPage(TypeIdentity type, int offset, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 1000);
        var indexes = _objectIndexesByType.TryGetValue(type, out var found) ? found : [];
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, indexes.Length);
        var count = Math.Min(pageSize, indexes.Length - offset);
        var page = new MemoryObjectInfo[count];
        for (var index = 0; index < count; index++)
        {
            page[index] = Project(indexes[offset + index]);
        }

        return new MemoryObjectPage(page, indexes.LongLength, offset, pageSize);
    }

    /// <summary>
    /// 查找从根或可达链起点到目标对象的一条引用路径。
    /// </summary>
    public MemoryReferencePath? GetReferencePath(ulong objectAddress)
    {
        if (!_objectIndexesByAddress.TryGetValue(objectAddress, out var target))
        {
            return null;
        }

        var roots = _roots;
        var rootSet = _roots.ToHashSet();
        var parent = new Dictionary<int, int>();
        var queue = new Queue<int>(roots);
        foreach (var root in roots)
        {
            parent.TryAdd(root, -1);
        }

        while (queue.TryDequeue(out var current))
        {
            if (current == target)
            {
                return BuildPath(target, parent);
            }

            if (!_edges.TryGetValue(current, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (parent.TryAdd(child, current))
                {
                    queue.Enqueue(child);
                }
            }
        }

        var reverseEdges = GetOrBuildReverseEdges();
        var next = new Dictionary<int, int> { [target] = -1 };
        queue.Enqueue(target);
        while (queue.TryDequeue(out var current))
        {
            if (rootSet.Contains(current) || !reverseEdges.TryGetValue(current, out var parents) || parents.Length == 0)
            {
                var path = new List<MemoryObjectInfo>();
                for (var cursor = current; cursor >= 0; cursor = next[cursor])
                {
                    path.Add(Project(cursor));
                }

                return new MemoryReferencePath(objectAddress, path);
            }

            foreach (var candidate in parents)
            {
                if (next.TryAdd(candidate, current))
                {
                    queue.Enqueue(candidate);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 快照索引输入行。
    /// </summary>
    internal readonly record struct ObjectRow(ulong Address, TypeIdentity Type, long SizeBytes);

    private Dictionary<int, int[]> BuildEdges(IReadOnlyDictionary<ulong, IReadOnlyList<ulong>>? source)
    {
        var result = new Dictionary<int, int[]>();
        if (source is null)
        {
            return result;
        }

        foreach (var pair in source)
        {
            if (!_objectIndexesByAddress.TryGetValue(pair.Key, out var parent))
            {
                continue;
            }

            var children = pair.Value
                .Select(address => _objectIndexesByAddress.TryGetValue(address, out var child) ? child : -1)
                .Where(child => child >= 0)
                .Distinct()
                .ToArray();
            if (children.Length > 0)
            {
                result[parent] = children;
            }
        }

        return result;
    }

    private MemoryTypeSummary[] BuildTypeSummaries()
    {
        var count = new long[_types.Length];
        var size = new long[_types.Length];
        foreach (var row in _objects)
        {
            count[row.TypeIndex]++;
            size[row.TypeIndex] = checked(size[row.TypeIndex] + row.SizeBytes);
        }

        return Enumerable.Range(0, _types.Length)
            .Select(index => new MemoryTypeSummary(_types[index], count[index], size[index]))
            .OrderByDescending(summary => summary.TotalSizeBytes)
            .ThenBy(summary => summary.Type.TypeName, StringComparer.Ordinal)
            .ToArray();
    }

    private Dictionary<int, int[]> GetOrBuildReverseEdges()
    {
        lock (_reverseEdgesSync)
        {
            if (_reverseEdges is not null)
            {
                return _reverseEdges;
            }

            _reverseIndexBuildStarting?.Invoke();
            var reverse = new Dictionary<int, List<int>>();
            foreach (var (parent, children) in _edges)
            {
                foreach (var child in children)
                {
                    if (!reverse.TryGetValue(child, out var parents))
                    {
                        parents = [];
                        reverse.Add(child, parents);
                    }

                    parents.Add(parent);
                }
            }

            _reverseEdges = reverse.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
            Interlocked.Increment(ref _reverseIndexBuildCount);
            return _reverseEdges;
        }
    }

    private MemoryReferencePath BuildPath(int target, Dictionary<int, int> parent)
    {
        var path = new List<MemoryObjectInfo>();
        for (var cursor = target; cursor >= 0; cursor = parent[cursor])
        {
            path.Add(Project(cursor));
        }

        path.Reverse();
        return new MemoryReferencePath(_objects[target].Address, path);
    }

    private MemoryObjectInfo Project(int index)
    {
        var row = _objects[index];
        return new MemoryObjectInfo(row.Address, _types[row.TypeIndex], row.SizeBytes);
    }

    private readonly record struct IndexedObjectRow(ulong Address, int TypeIndex, long SizeBytes);
}
