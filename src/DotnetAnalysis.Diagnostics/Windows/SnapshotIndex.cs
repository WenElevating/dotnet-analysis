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
    private readonly Dictionary<int, MemoryRetentionRoot[]> _retentionRootsByObjectIndex;
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
        Action? reverseIndexBuildStarting = null,
        IReadOnlyList<RetentionRootRow>? retentionRoots = null)
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
        _retentionRootsByObjectIndex = BuildRetentionRoots(roots, retentionRoots);
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
        var retentionPaths = GetRetentionPaths(objectAddress, maxPathCount: 1);
        return retentionPaths is null
            ? null
            : new MemoryReferencePath(objectAddress, retentionPaths.Paths[0].Objects);
    }

    /// <summary>
    /// 查找最多指定数量的不同 GC 根到目标对象的保留路径，并按根证据强度稳定排序。
    /// </summary>
    /// <param name="objectAddress">目标对象在快照中的地址。</param>
    /// <param name="maxPathCount">最多返回的路径数，范围为 1 至 16。</param>
    /// <returns>存在 GC 根路径时返回结果；对象未知或没有根证据时返回空。</returns>
    /// <exception cref="ArgumentOutOfRangeException">最大路径数不在 1 至 16 范围内时引发。</exception>
    public MemoryRetentionPathResult? GetRetentionPaths(
        ulong objectAddress,
        int maxPathCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPathCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPathCount, 16);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_objectIndexesByAddress.TryGetValue(objectAddress, out var target)
            || _retentionRootsByObjectIndex.Count == 0)
        {
            return null;
        }

        var reverseEdges = GetOrBuildReverseEdges(cancellationToken);
        var next = new Dictionary<int, int> { [target] = -1 };
        var queue = new Queue<int>();
        var paths = new List<MemoryRetentionPath>();
        queue.Enqueue(target);
        while (queue.TryDequeue(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_retentionRootsByObjectIndex.TryGetValue(current, out var roots))
            {
                var objects = BuildPathObjects(current, next);
                foreach (var root in roots)
                {
                    AddTopPath(paths, new MemoryRetentionPath(root, objects), maxPathCount);
                }
            }

            if (!reverseEdges.TryGetValue(current, out var parents))
            {
                continue;
            }

            foreach (var candidate in parents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (next.TryAdd(candidate, current))
                {
                    queue.Enqueue(candidate);
                }
            }
        }

        if (paths.Count == 0)
        {
            return null;
        }

        return new MemoryRetentionPathResult(objectAddress, paths);
    }

    /// <summary>
    /// 快照索引输入行。
    /// </summary>
    internal readonly record struct ObjectRow(ulong Address, TypeIdentity Type, long SizeBytes);

    /// <summary>
    /// 表示索引构造输入中的对象地址及其 GC 根证据。
    /// </summary>
    internal readonly record struct RetentionRootRow(ulong ObjectAddress, MemoryRetentionRoot Root);

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

    /// <summary>
    /// 将旧 GCDump 根地址和具有证据的新根描述统一为按对象索引查询的根表。
    /// </summary>
    private Dictionary<int, MemoryRetentionRoot[]> BuildRetentionRoots(
        IReadOnlyList<ulong>? roots,
        IReadOnlyList<RetentionRootRow>? retentionRoots)
    {
        var grouped = new Dictionary<int, List<MemoryRetentionRoot>>();
        foreach (var address in roots ?? [])
        {
            AddRetentionRoot(
                grouped,
                address,
                new MemoryRetentionRoot(MemoryRootKind.Unknown, MemoryRootFlags.None, null, null));
        }

        foreach (var retentionRoot in retentionRoots ?? [])
        {
            ArgumentNullException.ThrowIfNull(retentionRoot.Root);
            if ((retentionRoot.Root.Flags & MemoryRootFlags.WeakReference) != 0)
            {
                continue;
            }
            AddRetentionRoot(grouped, retentionRoot.ObjectAddress, retentionRoot.Root);
        }

        return grouped.ToDictionary(
            pair => pair.Key,
            pair => pair.Value
                .Distinct()
                .OrderBy(GetRootPriority)
                .ThenBy(root => root.FunctionName, StringComparer.Ordinal)
                .ToArray());
    }

    /// <summary>
    /// 将一个根证据加入其对应对象索引；指向快照外对象的根会被忽略。
    /// </summary>
    private void AddRetentionRoot(
        Dictionary<int, List<MemoryRetentionRoot>> grouped,
        ulong address,
        MemoryRetentionRoot root)
    {
        if (!_objectIndexesByAddress.TryGetValue(address, out var objectIndex))
        {
            return;
        }

        if (!grouped.TryGetValue(objectIndex, out var entries))
        {
            entries = [];
            grouped.Add(objectIndex, entries);
        }

        entries.Add(root);
    }

    /// <summary>
    /// 将根到目标的索引链投影为稳定的对象路径。
    /// </summary>
    private List<MemoryObjectInfo> BuildPathObjects(int root, Dictionary<int, int> next)
    {
        var path = new List<MemoryObjectInfo>();
        for (var cursor = root; cursor >= 0; cursor = next[cursor])
        {
            path.Add(Project(cursor));
        }

        return path;
    }

    /// <summary>
    /// 计算根证据的显示优先级；函数已验证的栈根优先于其他根。
    /// </summary>
    private static int GetRootPriority(MemoryRetentionPath path) => GetRootPriority(path.Root);

    /// <summary>
    /// 计算根证据的显示优先级；数值越小表示越值得优先展示。
    /// </summary>
    private static int GetRootPriority(MemoryRetentionRoot root) =>
        root.Kind switch
        {
            MemoryRootKind.Stack when root.FunctionName is not null => 0,
            MemoryRootKind.Stack => 1,
            MemoryRootKind.Handle => 2,
            MemoryRootKind.Finalizer => 3,
            MemoryRootKind.Other => 4,
            _ => 5
        };

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

    /// <summary>
    /// 在候选集中仅保留排序最靠前的有限路径，避免根数远大于请求上限时无界累积对象链。
    /// </summary>
    private static void AddTopPath(List<MemoryRetentionPath> paths, MemoryRetentionPath candidate, int maximumCount)
    {
        var insertionIndex = 0;
        while (insertionIndex < paths.Count && ComparePaths(paths[insertionIndex], candidate) <= 0)
        {
            insertionIndex++;
        }

        if (insertionIndex >= maximumCount)
        {
            return;
        }

        paths.Insert(insertionIndex, candidate);
        if (paths.Count > maximumCount)
        {
            paths.RemoveAt(paths.Count - 1);
        }
    }

    /// <summary>
    /// 使用公开的固定排序规则比较两条保留路径。
    /// </summary>
    private static int ComparePaths(MemoryRetentionPath left, MemoryRetentionPath right)
    {
        var priority = GetRootPriority(left).CompareTo(GetRootPriority(right));
        if (priority != 0)
        {
            return priority;
        }

        var length = left.Objects.Count.CompareTo(right.Objects.Count);
        if (length != 0)
        {
            return length;
        }

        var address = left.Objects[0].Address.CompareTo(right.Objects[0].Address);
        return address != 0
            ? address
            : string.Compare(left.Root.FunctionName, right.Root.FunctionName, StringComparison.Ordinal);
    }

    private Dictionary<int, int[]> GetOrBuildReverseEdges(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_reverseEdgesSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                    cancellationToken.ThrowIfCancellationRequested();
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

    private MemoryObjectInfo Project(int index)
    {
        var row = _objects[index];
        return new MemoryObjectInfo(row.Address, _types[row.TypeIndex], row.SizeBytes);
    }

    private readonly record struct IndexedObjectRow(ulong Address, int TypeIndex, long SizeBytes);
}
