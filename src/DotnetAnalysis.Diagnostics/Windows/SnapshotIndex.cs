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
    private MemoryDominatorInfo[]? _dominators;
    private int _reverseIndexBuildCount;

    /// <summary>
    /// 从对象行以及可选的地址引用边创建快照索引。
    /// </summary>
    /// <exception cref="DiagnosticsException">对象地址重复或引用边包含未知非零端点时引发。</exception>
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
            if (!_objectIndexesByAddress.TryAdd(row.Address, index))
            {
                throw CreateIndexBuildFailure("快照对象地址重复。");
            }
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
    /// 为 Diagnostics 内部工件写入器导出不可变索引数据；调用方不得向上层泄漏该基础设施模型。
    /// </summary>
    internal SnapshotIndexArtifactData ExportArtifactData()
    {
        var objects = _objects
            .Select(row => new ObjectRow(row.Address, _types[row.TypeIndex], row.SizeBytes))
            .ToArray();
        var edges = _edges.ToDictionary(
            pair => _objects[pair.Key].Address,
            pair => (IReadOnlyList<ulong>)pair.Value.Select(index => _objects[index].Address).ToArray());
        var roots = _retentionRootsByObjectIndex
            .SelectMany(pair => pair.Value.Select(root => new RetentionRootRow(_objects[pair.Key].Address, root)))
            .ToArray();
        return new SnapshotIndexArtifactData(objects, edges, roots);
    }

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
    /// 按保留大小降序读取指定页的支配树结果；首次查询时在当前索引上构建虚拟超级根支配树。
    /// </summary>
    /// <param name="offset">零基对象偏移量。</param>
    /// <param name="pageSize">每页对象数，范围为 1 至 1000。</param>
    /// <param name="cancellationToken">取消本次构建或投影的令牌。</param>
    /// <returns>按保留大小排序的对象分页。</returns>
    public MemoryDominatorPage GetDominatorPage(
        int offset,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 1000);
        var dominators = GetOrBuildDominators(cancellationToken);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, dominators.Length);
        var count = Math.Min(pageSize, dominators.Length - offset);
        return new MemoryDominatorPage(dominators.AsSpan(offset, count).ToArray(), dominators.LongLength, offset, pageSize);
    }

    /// <summary>
    /// 快照索引输入行。
    /// </summary>
    internal readonly record struct ObjectRow(ulong Address, TypeIdentity Type, long SizeBytes);

    /// <summary>
    /// 表示索引构造输入中的对象地址及其 GC 根证据。
    /// </summary>
    internal readonly record struct RetentionRootRow(ulong ObjectAddress, MemoryRetentionRoot Root);

    /// <summary>
    /// 表示基础索引写入器使用的对象、边和根证据快照。
    /// </summary>
    internal sealed record SnapshotIndexArtifactData(
        IReadOnlyList<ObjectRow> Objects,
        IReadOnlyDictionary<ulong, IReadOnlyList<ulong>> Edges,
        IReadOnlyList<RetentionRootRow> Roots);

    /// <summary>
    /// 将地址边转换为连续对象索引边；零地址表示空引用并被忽略，未知非零端点视为不完整快照。
    /// </summary>
    /// <param name="source">按源对象地址分组的目标地址列表。</param>
    /// <returns>保持输入顺序并去重的对象索引邻接表。</returns>
    /// <exception cref="DiagnosticsException">任一非零源或目标地址不在对象表中时引发。</exception>
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
                if (pair.Key == 0)
                {
                    continue;
                }

                throw CreateIndexBuildFailure("快照引用边包含未知非零源地址。");
            }

            var children = new List<int>(pair.Value.Count);
            var seenChildren = new HashSet<int>();
            foreach (var address in pair.Value)
            {
                if (address == 0)
                {
                    continue;
                }

                if (!_objectIndexesByAddress.TryGetValue(address, out var child))
                {
                    throw CreateIndexBuildFailure("快照引用边包含未知非零目标地址。");
                }

                if (seenChildren.Add(child))
                {
                    children.Add(child);
                }
            }

            if (children.Count > 0)
            {
                result[parent] = children.ToArray();
            }
        }

        return result;
    }

    /// <summary>
    /// 将内部图完整性错误转换为上层可稳定分类的索引构建失败。
    /// </summary>
    /// <param name="message">描述损坏输入边界的内部错误信息。</param>
    /// <returns>携带 <see cref="DiagnosticsErrorCode.SnapshotIndexBuildFailed"/> 的诊断异常。</returns>
    private static DiagnosticsException CreateIndexBuildFailure(string message) => new(
        DiagnosticsErrorCode.SnapshotIndexBuildFailed,
        "无法构建完整的快照内存索引。",
        new InvalidDataException(message));

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

    /// <summary>
    /// 以所有非弱根连接的虚拟超级根为入口，使用非递归 Lengauer-Tarjan 算法构建支配树并累计保留大小。
    /// </summary>
    private MemoryDominatorInfo[] GetOrBuildDominators(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_reverseEdgesSync)
        {
            if (_dominators is not null)
            {
                return _dominators;
            }

            var rootIndexes = _retentionRootsByObjectIndex.Keys.OrderBy(index => index).ToArray();
            if (rootIndexes.Length == 0)
            {
                _dominators = [];
                return _dominators;
            }

            var objectCount = _objects.Length;
            var virtualRoot = objectCount;
            var dfsNumberByObject = new int[objectCount + 1];
            // DFS 编号从 1 开始，且虚拟根额外占用一个编号，因此需要为全部对象再预留一个槽位。
            var objectByDfsNumber = new int[objectCount + 2];
            var parent = new int[objectCount + 2];
            var dfsCount = 1;
            dfsNumberByObject[virtualRoot] = 1;
            objectByDfsNumber[1] = virtualRoot;

            var stack = new Stack<DfsFrame>();
            stack.Push(new DfsFrame(virtualRoot, rootIndexes));
            while (stack.TryPeek(out var frame))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!frame.TryMoveNext(out var child))
                {
                    _ = stack.Pop();
                    continue;
                }

                if (dfsNumberByObject[child] != 0)
                {
                    continue;
                }

                var childDfsNumber = ++dfsCount;
                dfsNumberByObject[child] = childDfsNumber;
                objectByDfsNumber[childDfsNumber] = child;
                parent[childDfsNumber] = dfsNumberByObject[frame.ObjectIndex];
                var targets = _edges.TryGetValue(child, out var found) ? found : [];
                stack.Push(new DfsFrame(child, targets));
            }

            var semi = new int[dfsCount + 1];
            var immediateDominator = new int[dfsCount + 1];
            var ancestor = new int[dfsCount + 1];
            var label = new int[dfsCount + 1];
            var buckets = new List<int>[dfsCount + 1];
            for (var index = 1; index <= dfsCount; index++)
            {
                semi[index] = index;
                label[index] = index;
                buckets[index] = [];
            }

            var reverseEdges = GetOrBuildReverseEdges(cancellationToken);
            for (var current = dfsCount; current >= 2; current--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var objectIndex = objectByDfsNumber[current];
                if (reverseEdges.TryGetValue(objectIndex, out var predecessors))
                {
                    foreach (var predecessor in predecessors)
                    {
                        var predecessorDfs = dfsNumberByObject[predecessor];
                        if (predecessorDfs == 0)
                        {
                            continue;
                        }

                        var evaluated = Evaluate(predecessorDfs, ancestor, label, semi);
                        semi[current] = Math.Min(semi[current], semi[evaluated]);
                    }
                }

                if (_retentionRootsByObjectIndex.ContainsKey(objectIndex))
                {
                    semi[current] = 1;
                }

                buckets[semi[current]].Add(current);
                ancestor[current] = parent[current];
                var parentBucket = buckets[parent[current]];
                foreach (var candidate in parentBucket)
                {
                    var evaluated = Evaluate(candidate, ancestor, label, semi);
                    immediateDominator[candidate] = semi[evaluated] < semi[candidate]
                        ? evaluated
                        : parent[current];
                }

                parentBucket.Clear();
            }

            for (var current = 2; current <= dfsCount; current++)
            {
                if (immediateDominator[current] != semi[current])
                {
                    immediateDominator[current] = immediateDominator[immediateDominator[current]];
                }
            }

            var retained = new long[dfsCount + 1];
            for (var current = 2; current <= dfsCount; current++)
            {
                retained[current] = _objects[objectByDfsNumber[current]].SizeBytes;
            }

            for (var current = dfsCount; current >= 2; current--)
            {
                retained[immediateDominator[current]] = checked(retained[immediateDominator[current]] + retained[current]);
            }

            _dominators = Enumerable.Range(2, dfsCount - 1)
                .Select(current =>
                {
                    var objectIndex = objectByDfsNumber[current];
                    ulong? directDominator = immediateDominator[current] == 1
                        ? null
                        : _objects[objectByDfsNumber[immediateDominator[current]]].Address;
                    var root = _retentionRootsByObjectIndex.TryGetValue(objectIndex, out var roots)
                        ? roots[0]
                        : null;
                    return new MemoryDominatorInfo(Project(objectIndex), retained[current], directDominator, root);
                })
                .OrderByDescending(info => info.RetainedSizeBytes)
                .ThenByDescending(info => info.DirectSizeBytes)
                .ThenBy(info => info.ObjectInfo.Address)
                .ToArray();
            return _dominators;
        }
    }

    /// <summary>
    /// 在 Lengauer-Tarjan 并查集中计算一个 DFS 节点的半支配标签，不使用递归以避免大图栈溢出。
    /// </summary>
    private static int Evaluate(int node, int[] ancestor, int[] label, int[] semi)
    {
        if (ancestor[node] == 0)
        {
            return label[node];
        }

        var path = new List<int>();
        for (var cursor = node; ancestor[cursor] != 0 && ancestor[ancestor[cursor]] != 0; cursor = ancestor[cursor])
        {
            path.Add(cursor);
        }

        for (var index = path.Count - 1; index >= 0; index--)
        {
            var current = path[index];
            var currentAncestor = ancestor[current];
            if (semi[label[currentAncestor]] < semi[label[current]])
            {
                label[current] = label[currentAncestor];
            }

            ancestor[current] = ancestor[currentAncestor];
        }

        var parent = ancestor[node];
        return semi[label[parent]] >= semi[label[node]] ? label[node] : label[parent];
    }

    private MemoryObjectInfo Project(int index)
    {
        var row = _objects[index];
        return new MemoryObjectInfo(row.Address, _types[row.TypeIndex], row.SizeBytes);
    }

    private readonly record struct IndexedObjectRow(ulong Address, int TypeIndex, long SizeBytes);

    /// <summary>
    /// 维护非递归 DFS 帧及下一条待访问边的位置。
    /// </summary>
    private sealed class DfsFrame
    {
        private readonly int[] _targets;
        private int _nextTarget;

        /// <summary>
        /// 创建一个 DFS 遍历帧。
        /// </summary>
        public DfsFrame(int objectIndex, int[] targets)
        {
            ObjectIndex = objectIndex;
            _targets = targets;
        }

        /// <summary>
        /// 当前对象索引。
        /// </summary>
        public int ObjectIndex { get; }

        /// <summary>
        /// 读取下一条待访问边。
        /// </summary>
        public bool TryMoveNext(out int target)
        {
            if (_nextTarget >= _targets.Length)
            {
                target = default;
                return false;
            }

            target = _targets[_nextTarget++];
            return true;
        }
    }
}
