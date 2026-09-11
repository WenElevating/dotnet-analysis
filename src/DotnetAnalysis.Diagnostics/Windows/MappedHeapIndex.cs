using System.Text;
using System.Text.Json;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 从已发布堆索引工件按需读取类型统计、对象分页、引用链和派生分析，不把整图常驻在句柄中。
/// </summary>
internal sealed class MappedHeapIndex : IDisposable
{
    private const int MaximumObjectPageSize = 1_000;
    private const int ObjectRecordBytes = sizeof(ulong) + sizeof(int) + sizeof(long);
    private readonly string _directory;
    private readonly RetentionPathQueryLimits _retentionPathQueryLimits;
    private readonly TimeProvider _timeProvider;
    private readonly object _derivedAnalysisSync = new();
    private readonly CancellationTokenSource _disposeCancellationSource = new();
    private MemoryTypeSummary[]? _typeSummaries;
    private TypeIdentity[]? _types;
    private Dictionary<TypeIdentity, MemoryTypeSummary>? _typeSummariesByType;
    private Task<HeapDerivedAnalysisArtifact>? _derivedAnalysisTask;

    /// <summary>
    /// 使用已验证的 v3 工件目录创建按需读取器。
    /// </summary>
    public MappedHeapIndex(string directory)
        : this(directory, RetentionPathQueryLimits.Default, TimeProvider.System)
    {
    }

    /// <summary>
    /// 使用指定路径查询预算和单调时钟创建按需读取器；该入口仅供确定性资源边界测试使用。
    /// </summary>
    /// <param name="directory">已经完整校验并发布的 v3 工件目录。</param>
    /// <param name="retentionPathQueryLimits">单次保留路径查询的对象、边、队列和时间预算。</param>
    /// <param name="timeProvider">提供单调时间戳的时钟。</param>
    internal MappedHeapIndex(
        string directory,
        RetentionPathQueryLimits retentionPathQueryLimits,
        TimeProvider timeProvider)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _retentionPathQueryLimits = retentionPathQueryLimits;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// 读取已序列化的类型统计；该元数据规模受类型数而非对象数限制。
    /// </summary>
    public IReadOnlyList<MemoryTypeSummary> TypeSummaries => _typeSummaries ??= ReadTypeSummaries();

    /// <summary>
    /// 根据已发布统计维持公开的小快照全量/大快照分页契约。
    /// </summary>
    public MemorySnapshotObjectAccessMode ObjectAccessMode => MemorySnapshotAnalysis.DetermineObjectAccessMode(TypeSummaries.Sum(item => item.ObjectCount));

    /// <summary>
    /// 小快照时读取单个类型的全部对象；大快照稳定拒绝全量读取。
    /// </summary>
    public IReadOnlyList<MemoryObjectInfo> GetObjects(TypeIdentity type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (ObjectAccessMode is MemorySnapshotObjectAccessMode.Paged)
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.SnapshotTooLargeForFullEnumeration, "The snapshot is too large for full object enumeration. Use GetObjectsPageAsync instead.");
        }

        var summary = TypeSummaries.SingleOrDefault(item => item.Type == type);
        if (summary is null)
        {
            return [];
        }

        var objects = new MemoryObjectInfo[checked((int)summary.ObjectCount)];
        for (var offset = 0; offset < objects.Length; offset += MaximumObjectPageSize)
        {
            var page = GetPage(type, offset, Math.Min(MaximumObjectPageSize, objects.Length - offset));
            for (var index = 0; index < page.Objects.Count; index++)
            {
                objects[offset + index] = page.Objects[index];
            }
        }

        return objects;
    }

    /// <summary>
    /// 通过 objects-by-type 工件直接定位所请求类型的对象标识，只投影当前页而不扫描完整对象表。
    /// </summary>
    public MemoryObjectPage GetPage(TypeIdentity type, int offset, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaximumObjectPageSize);
        var typeIndex = Array.FindIndex(Types, item => item == type);
        if (typeIndex < 0)
        {
            return new MemoryObjectPage([], 0, offset == 0 ? 0 : throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset cannot exceed object count."), pageSize);
        }

        if (!TypeSummariesByType.TryGetValue(type, out var summary))
        {
            return new MemoryObjectPage([], 0, offset == 0 ? 0 : throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset cannot exceed object count."), pageSize);
        }

        if (offset > summary.ObjectCount)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset cannot exceed object count.");
        }

        var count = (int)Math.Min(pageSize, summary.ObjectCount - offset);
        var firstObjectIndex = 0L;
        for (var index = 0; index < typeIndex; index++)
        {
            if (TypeSummariesByType.TryGetValue(Types[index], out var precedingSummary))
            {
                firstObjectIndex += precedingSummary.ObjectCount;
            }
        }

        var objectPath = Path.Combine(_directory, "objects.bin");
        var page = new MemoryObjectInfo[count];
        using var objectIds = new BinaryReader(File.OpenRead(Path.Combine(_directory, "objects-by-type.bin")));
        using var objects = new BinaryReader(File.OpenRead(objectPath), Encoding.UTF8, leaveOpen: false);
        objectIds.BaseStream.Position = checked((firstObjectIndex + offset) * sizeof(int));
        for (var index = 0; index < count; index++)
        {
            var objectId = objectIds.ReadInt32();
            var (address, currentTypeIndex, size) = ReadObject(objects, objectId);
            if (currentTypeIndex != typeIndex)
            {
                throw new InvalidDataException("按类型对象索引与对象工件不一致。");
            }

            page[index] = new MemoryObjectInfo(address, type, size);
        }

        return new MemoryObjectPage(page, summary.ObjectCount, offset, pageSize);
    }

    /// <summary>
    /// 路径查询仅按需读取反向 CSR 工件，返回后不保留对象、边或反向边集合。
    /// </summary>
    public MemoryReferencePath? GetReferencePath(ulong address, CancellationToken cancellationToken)
    {
        var paths = GetRetentionPaths(address, 1, cancellationToken);
        return paths is null ? null : new MemoryReferencePath(address, paths.Paths[0].Objects);
    }

    /// <summary>
    /// 查询保留路径时按需遍历反向 CSR，调用方取消仅作用于本次遍历。
    /// </summary>
    public MemoryRetentionPathResult? GetRetentionPaths(ulong address, int maximum, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximum, 16);
        cancellationToken.ThrowIfCancellationRequested();
        var budget = new RetentionPathQueryBudget(_retentionPathQueryLimits, _timeProvider);
        if (!TryFindObjectId(address, out var target)) return null;
        budget.Checkpoint();
        using var roots = new RootEvidenceReader(_directory, ObjectCount);
        if (!roots.HasAnyEvidence) return null;
        using var reverseOffsets = new HeapMappedReadWindow(Path.Combine(_directory, "reverse-offsets.bin"));
        using var reverseTargets = new HeapMappedReadWindow(Path.Combine(_directory, "reverse-targets.bin"));
        using var objects = new HeapMappedReadWindow(Path.Combine(_directory, "objects.bin"));
        budget.EnsureCanVisit(0);
        var next = new Dictionary<int, int> { [target] = -1 };
        var queue = new Queue<int>();
        var paths = new List<MemoryRetentionPath>();
        budget.ConsumeQueueOperation();
        queue.Enqueue(target);
        while (queue.TryDequeue(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.ConsumeQueueOperation();
            foreach (var root in roots.Read(current))
            {
                budget.Checkpoint();
                if (root.Flags.HasFlag(MemoryRootFlags.WeakReference))
                {
                    continue;
                }

                AddPath(paths, new MemoryRetentionPath(root, BuildPath(current, next, objects)), maximum);
            }
            foreach (var parent in ReadReverseParentsWithinBudget(current, reverseOffsets, reverseTargets, budget))
            {
                if (next.ContainsKey(parent))
                {
                    continue;
                }

                budget.EnsureCanVisit(next.Count);
                budget.ConsumeQueueOperation();
                next.Add(parent, current);
                queue.Enqueue(parent);
            }
        }
        return paths.Count == 0 ? null : new MemoryRetentionPathResult(address, paths);
    }

    /// <summary>
    /// 读取支配树分页；首次调用后台构建并原子发布独立 .heapderived 工件，调用方取消仅取消等待。
    /// </summary>
    /// <param name="offset">零基结果偏移量。</param>
    /// <param name="size">每页对象数，范围为 1 至 1000。</param>
    /// <param name="cancellationToken">仅取消当前等待或投影操作的令牌。</param>
    /// <returns>按 retained size 排序的支配对象页。</returns>
    public async Task<MemoryDominatorPage> GetDominatorPageAsync(int offset, int size, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 1000);
        var task = GetOrStartDerivedAnalysis();
        HeapDerivedAnalysisArtifact artifact;
        try
        {
            artifact = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch when (task.IsFaulted || task.IsCanceled)
        {
            lock (_derivedAnalysisSync)
            {
                if (ReferenceEquals(_derivedAnalysisTask, task))
                {
                    _derivedAnalysisTask = null;
                }
            }

            throw;
        }

        var entries = artifact.ReadPage(offset, size, cancellationToken);
        using var roots = new RootEvidenceReader(_directory, ObjectCount);
        using var objects = new BinaryReader(File.OpenRead(Path.Combine(_directory, "objects.bin")), Encoding.UTF8, leaveOpen: false);
        var values = new MemoryDominatorInfo[entries.Count];
        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[index];
            var (address, typeIndex, sizeBytes) = ReadObject(objects, entry.ObjectId);
            var objectInfo = new MemoryObjectInfo(
                address,
                Types[typeIndex],
                sizeBytes);
            ulong? immediateDominatorAddress = entry.ImmediateDominatorObjectId < 0
                ? null
                : ReadObject(objects, entry.ImmediateDominatorObjectId).Address;
            values[index] = new MemoryDominatorInfo(
                objectInfo,
                entry.RetainedSizeBytes,
                immediateDominatorAddress,
                roots.ReadBest(entry.ObjectId));
        }

        return new MemoryDominatorPage(values, artifact.ObjectCount, offset, size);
    }

    /// <summary>
    /// 此实现不持有长期文件句柄；释放时会取消仍在运行的派生分析，
    /// 使快照切换或服务停止不会让旧快照继续占用 CPU、映射窗口和派生工件工作区。
    /// </summary>
    public void Dispose()
    {
        _disposeCancellationSource.Cancel();
    }

    private TypeIdentity[] Types => _types ??= ReadTypes();

    /// <summary>
    /// 当前对象总数从固定宽度对象工件长度推导，避免为根偏移表另行持久化全局计数。
    /// </summary>
    private int ObjectCount
    {
        get
        {
            var length = new FileInfo(Path.Combine(_directory, "objects.bin")).Length;
            if (length % ObjectRecordBytes != 0 || length / ObjectRecordBytes > int.MaxValue)
            {
                throw new InvalidDataException("对象工件长度无效。");
            }

            return checked((int)(length / ObjectRecordBytes));
        }
    }

    /// <summary>
    /// 按类型身份索引受限规模的统计元数据，供 objects-by-type 的页偏移计算复用。
    /// </summary>
    private Dictionary<TypeIdentity, MemoryTypeSummary> TypeSummariesByType => _typeSummariesByType ??=
        TypeSummaries.ToDictionary(item => item.Type);

    private MemoryTypeSummary[] ReadTypeSummaries()
    {
        var text = File.ReadAllText(Path.Combine(_directory, "type-summary.bin"));
        return JsonSerializer.Deserialize<MemoryTypeSummary[]>(text) ?? throw new InvalidDataException("堆索引类型统计工件无效。");
    }

    private TypeIdentity[] ReadTypes()
    {
        using var reader = new BinaryReader(File.OpenRead(Path.Combine(_directory, "types.bin")));
        var count = reader.ReadInt32();
        if (count < 0 || count > 10_000_000)
        {
            throw new InvalidDataException("堆索引类型表无效。");
        }

        var values = new TypeIdentity[count];
        for (var index = 0; index < count; index++)
        {
            var name = reader.ReadString();
            var assembly = reader.ReadString();
            values[index] = new TypeIdentity(name, string.IsNullOrEmpty(assembly) ? null : assembly);
        }
        return values;
    }

    private bool TryFindObjectId(ulong address, out int objectId)
    {
        using var stream = new FileStream(Path.Combine(_directory, "address-to-id.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream);
        var low = 0L;
        var high = stream.Length / (sizeof(ulong) + sizeof(int)) - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            stream.Position = middle * (sizeof(ulong) + sizeof(int));
            var candidate = reader.ReadUInt64();
            var id = reader.ReadInt32();
            if (candidate == address)
            {
                objectId = id;
                return true;
            }
            if (candidate < address) low = middle + 1; else high = middle - 1;
        }
        objectId = default;
        return false;
    }

    /// <summary>
    /// 在打开的固定宽度对象工件中随机读取一个对象行；只触碰调用方请求的记录，不创建覆盖整份文件的映射视图。
    /// </summary>
    private static (ulong Address, int TypeIndex, long SizeBytes) ReadObject(BinaryReader reader, int objectId)
    {
        if (objectId < 0 || (long)(objectId + 1) * ObjectRecordBytes > reader.BaseStream.Length)
        {
            throw new InvalidDataException("对象标识超出对象工件范围。");
        }

        reader.BaseStream.Position = (long)objectId * ObjectRecordBytes;
        return (reader.ReadUInt64(), reader.ReadInt32(), reader.ReadInt64());
    }

    /// <summary>
    /// 在受限映射窗口中随机读取一个对象行；路径投影复用同一窗口，
    /// 避免在大量反向图节点间重复打开对象工件。
    /// </summary>
    /// <param name="objects">覆盖对象工件的受限映射读取器。</param>
    /// <param name="objectId">连续对象标识。</param>
    /// <returns>对象地址、类型索引和浅表大小。</returns>
    private static (ulong Address, int TypeIndex, long SizeBytes) ReadObject(HeapMappedReadWindow objects, int objectId)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (objectId < 0)
        {
            throw new InvalidDataException("对象标识超出对象工件范围。");
        }

        var offset = checked((long)objectId * ObjectRecordBytes);
        return (
            objects.ReadUInt64(offset),
            objects.ReadInt32(offset + sizeof(ulong)),
            objects.ReadInt64(offset + sizeof(ulong) + sizeof(int)));
    }

    /// <summary>
    /// 流式枚举指定对象的反向 CSR 父对象；高扇入节点不会先复制为全量 <c>int[]</c>，
    /// 路径查询的工作内存因此由已访问对象上限而非单个入边度数决定。
    /// </summary>
    /// <param name="objectId">目标对象的连续内部标识。</param>
    /// <param name="offsets">覆盖反向 CSR 偏移工件的受限映射读取器。</param>
    /// <param name="targets">覆盖反向 CSR 目标工件的受限映射读取器。</param>
    /// <returns>按 CSR 中的稳定顺序产生父对象标识的枚举。</returns>
    private static IEnumerable<int> ReadReverseParents(
        int objectId,
        HeapMappedReadWindow offsets,
        HeapMappedReadWindow targets) =>
        ReadReverseParentsWithinBudget(objectId, offsets, targets, budget: null);

    /// <summary>
    /// 流式枚举指定对象的反向 CSR 父对象，并在每次实际读取 target 前消费边扫描预算。
    /// </summary>
    /// <param name="objectId">目标对象的连续内部标识。</param>
    /// <param name="offsets">覆盖反向 CSR 偏移工件的受限映射读取器。</param>
    /// <param name="targets">覆盖反向 CSR 目标工件的受限映射读取器。</param>
    /// <param name="budget">可选的单次路径查询预算；结构测试直接枚举时可为空。</param>
    /// <returns>按 CSR 中的稳定顺序产生父对象标识的枚举。</returns>
    private static IEnumerable<int> ReadReverseParentsWithinBudget(
        int objectId,
        HeapMappedReadWindow offsets,
        HeapMappedReadWindow targets,
        RetentionPathQueryBudget? budget)
    {
        ArgumentNullException.ThrowIfNull(offsets);
        ArgumentNullException.ThrowIfNull(targets);
        if (objectId < 0)
        {
            throw new InvalidDataException("反向 CSR 对象标识超出偏移工件范围。");
        }

        var start = offsets.ReadInt64((long)objectId * sizeof(long));
        var end = offsets.ReadInt64((long)(objectId + 1) * sizeof(long));
        if (start < 0 || end < start)
        {
            throw new InvalidDataException("反向 CSR 偏移超出目标工件范围。");
        }

        for (var index = start; index < end; index++)
        {
            budget?.ConsumeEdgeScan();
            yield return targets.ReadInt32(checked(index * sizeof(int)));
        }
    }

    /// <summary>
    /// 定义单次映射保留路径查询允许消耗的确定性对象、边、队列和经过时间预算。
    /// </summary>
    internal readonly record struct RetentionPathQueryLimits
    {
        /// <summary>
        /// 创建一组全部为正值的查询预算。
        /// </summary>
        /// <param name="maximumVisitedObjects">最多登记的不同对象数，包含目标对象。</param>
        /// <param name="maximumScannedEdges">最多读取的反向 CSR 边数。</param>
        /// <param name="maximumQueueOperations">最多执行的入队与出队操作总数。</param>
        /// <param name="maximumDuration">从查询开始计算的最大经过时间。</param>
        public RetentionPathQueryLimits(
            int maximumVisitedObjects,
            long maximumScannedEdges,
            int maximumQueueOperations,
            TimeSpan maximumDuration)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumVisitedObjects, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumScannedEdges, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumQueueOperations, 1);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumDuration, TimeSpan.Zero);
            MaximumVisitedObjects = maximumVisitedObjects;
            MaximumScannedEdges = maximumScannedEdges;
            MaximumQueueOperations = maximumQueueOperations;
            MaximumDuration = maximumDuration;
        }

        /// <summary>生产默认最多登记 250,000 个对象、读取 2,000,000 条边并执行 500,000 次队列操作，最长 10 秒。</summary>
        public static RetentionPathQueryLimits Default { get; } = new(
            250_000,
            2_000_000,
            500_000,
            TimeSpan.FromSeconds(10));

        /// <summary>最多登记的不同对象数。</summary>
        public int MaximumVisitedObjects { get; }

        /// <summary>最多读取的反向 CSR 边数。</summary>
        public long MaximumScannedEdges { get; }

        /// <summary>最多执行的入队与出队操作总数。</summary>
        public int MaximumQueueOperations { get; }

        /// <summary>查询允许消耗的最大经过时间。</summary>
        public TimeSpan MaximumDuration { get; }
    }

    /// <summary>
    /// 在路径遍历中集中核算资源预算；任何边界耗尽都映射为同一稳定诊断错误。
    /// </summary>
    private sealed class RetentionPathQueryBudget
    {
        private readonly RetentionPathQueryLimits _limits;
        private readonly TimeProvider _timeProvider;
        private readonly long _startedTimestamp;
        private long _scannedEdges;
        private int _queueOperations;

        /// <summary>
        /// 从当前单调时间戳开始核算指定预算。
        /// </summary>
        /// <param name="limits">单次查询预算。</param>
        /// <param name="timeProvider">提供经过时间计算的单调时钟。</param>
        public RetentionPathQueryBudget(RetentionPathQueryLimits limits, TimeProvider timeProvider)
        {
            _limits = limits;
            _timeProvider = timeProvider;
            _startedTimestamp = timeProvider.GetTimestamp();
        }

        /// <summary>
        /// 在字典登记和入队新对象前检查不同对象预算。
        /// </summary>
        /// <param name="currentVisitedObjects">当前已经登记的不同对象数。</param>
        public void EnsureCanVisit(int currentVisitedObjects)
        {
            Checkpoint();
            if (currentVisitedObjects >= _limits.MaximumVisitedObjects)
            {
                ThrowLimitReached();
            }
        }

        /// <summary>
        /// 在实际读取下一条反向 CSR target 前消费一次边扫描预算。
        /// </summary>
        public void ConsumeEdgeScan()
        {
            Checkpoint();
            if (_scannedEdges >= _limits.MaximumScannedEdges)
            {
                ThrowLimitReached();
            }

            _scannedEdges++;
        }

        /// <summary>
        /// 在执行一次队列入队或出队前消费工作预算。
        /// </summary>
        public void ConsumeQueueOperation()
        {
            Checkpoint();
            if (_queueOperations >= _limits.MaximumQueueOperations)
            {
                ThrowLimitReached();
            }

            _queueOperations++;
        }

        /// <summary>
        /// 在无其他计数器变化的根证据投影点检查单调截止时间。
        /// </summary>
        public void Checkpoint()
        {
            if (_timeProvider.GetElapsedTime(_startedTimestamp, _timeProvider.GetTimestamp()) >= _limits.MaximumDuration)
            {
                ThrowLimitReached();
            }
        }

        /// <summary>
        /// 把所有查询预算耗尽统一映射为稳定错误码，避免上层依赖内部限制类型。
        /// </summary>
        private static void ThrowLimitReached() => throw new DiagnosticsException(
            DiagnosticsErrorCode.SnapshotQueryLimitReached,
            "引用路径查询超过按需索引访问上限。");
    }

    /// <summary>
    /// 根据反向遍历构造的下一跳表投影根到目标对象路径；对象行由调用方持有的映射窗口按需读取。
    /// </summary>
    private List<MemoryObjectInfo> BuildPath(
        int root,
        Dictionary<int, int> next,
        HeapMappedReadWindow objects)
    {
        var values = new List<MemoryObjectInfo>();
        for (var current = root; current >= 0; current = next[current])
        {
            var (address, typeIndex, sizeBytes) = ReadObject(objects, current);
            values.Add(new MemoryObjectInfo(address, Types[typeIndex], sizeBytes));
        }
        return values;
    }

    private static int GetRootPriority(MemoryRetentionRoot root) => root.Kind switch
    {
        MemoryRootKind.Stack when root.FunctionName is not null => 0,
        MemoryRootKind.Stack => 1,
        MemoryRootKind.Handle => 2,
        MemoryRootKind.Finalizer => 3,
        MemoryRootKind.Other => 4,
        _ => 5
    };

    private static void AddPath(List<MemoryRetentionPath> paths, MemoryRetentionPath path, int maximum)
    {
        if (paths.Any(existing => existing.Root == path.Root && existing.Objects.SequenceEqual(path.Objects)))
        {
            return;
        }

        paths.Add(path);
        paths.Sort((left, right) =>
        {
            var priority = GetRootPriority(left.Root).CompareTo(GetRootPriority(right.Root));
            if (priority != 0) return priority;
            var length = left.Objects.Count.CompareTo(right.Objects.Count);
            return length != 0 ? length : left.Objects[0].Address.CompareTo(right.Objects[0].Address);
        });
        if (paths.Count > maximum) paths.RemoveAt(paths.Count - 1);
    }

    /// <summary>
    /// 在根偏移表和变长证据流上执行按 objectId 的有界读取；生命周期只覆盖单次查询或单页投影。
    /// </summary>
    private sealed class RootEvidenceReader : IDisposable
    {
        private const int MaximumEvidenceRecordBytes = 2 * 1_048_576 + 16;
        private readonly FileStream _offsetsStream;
        private readonly BinaryReader _offsets;
        private readonly FileStream _evidenceStream;
        private readonly BinaryReader _evidence;
        private readonly int _objectCount;

        /// <summary>
        /// 打开已发布的根工件，并验证偏移数量与对象数一一对应。
        /// </summary>
        public RootEvidenceReader(string directory, int objectCount)
        {
            _objectCount = objectCount;
            var offsetsStream = new FileStream(Path.Combine(directory, "roots-by-object.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var evidenceStream = new FileStream(Path.Combine(directory, "root-evidence.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            try
            {
                if (offsetsStream.Length != checked(((long)objectCount + 1) * sizeof(long)))
                {
                    throw new InvalidDataException("根偏移工件长度无效。");
                }

                _offsetsStream = offsetsStream;
                _evidenceStream = evidenceStream;
                _offsets = new BinaryReader(_offsetsStream, Encoding.UTF8, leaveOpen: true);
                _evidence = new BinaryReader(_evidenceStream, Encoding.UTF8, leaveOpen: true);
                HasAnyEvidence = ReadOffset(objectCount) != 0;
            }
            catch
            {
                evidenceStream.Dispose();
                offsetsStream.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 是否存在至少一条已验证的非弱根证据。
        /// </summary>
        public bool HasAnyEvidence { get; }

        /// <summary>
        /// 流式读取指定对象的根证据；不会扫描其他对象的记录，也不会为高根数对象建立完整集合。
        /// </summary>
        public IEnumerable<MemoryRetentionRoot> Read(int objectId)
        {
            if ((uint)objectId >= (uint)_objectCount)
            {
                throw new InvalidDataException("根证据对象标识超出范围。");
            }

            var start = ReadOffset(objectId);
            var end = ReadOffset(objectId + 1);
            if (start < 0 || end < start || end > _evidenceStream.Length)
            {
                throw new InvalidDataException("根证据偏移无效。");
            }

            if (start == end)
            {
                yield break;
            }

            _evidenceStream.Position = start;
            while (_evidenceStream.Position < end)
            {
                yield return ReadRoot(end);
            }

            if (_evidenceStream.Position != end)
            {
                throw new InvalidDataException("根证据记录越过声明范围。");
            }

        }

        /// <summary>
        /// 返回指定对象按公开路径优先级排序后的首条根证据，供支配页摘要使用。
        /// </summary>
        public MemoryRetentionRoot? ReadBest(int objectId)
        {
            MemoryRetentionRoot? best = null;
            var bestPriority = int.MaxValue;
            foreach (var root in Read(objectId))
            {
                if (root.Flags.HasFlag(MemoryRootFlags.WeakReference))
                {
                    continue;
                }

                var priority = GetRootPriority(root);
                if (priority < bestPriority)
                {
                    best = root;
                    bestPriority = priority;
                }
            }

            return best;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _offsets.Dispose();
            _offsetsStream.Dispose();
            _evidence.Dispose();
            _evidenceStream.Dispose();
        }

        /// <summary>
        /// 读取指定 objectId 的证据流字节偏移量。
        /// </summary>
        private long ReadOffset(int objectId)
        {
            _offsetsStream.Position = (long)objectId * sizeof(long);
            return _offsets.ReadInt64();
        }

        /// <summary>
        /// 在当前对象的证据范围内读取一条完整记录并验证枚举与字符串边界。
        /// </summary>
        private MemoryRetentionRoot ReadRoot(long end)
        {
            if (end - _evidenceStream.Position < sizeof(byte) + sizeof(int))
            {
                throw new InvalidDataException("根证据记录截断。");
            }

            var kindValue = _evidence.ReadByte();
            if (!Enum.IsDefined((MemoryRootKind)kindValue))
            {
                throw new InvalidDataException("根证据类别无效。");
            }

            var flags = (MemoryRootFlags)_evidence.ReadInt32();
            var function = ReadNullableString(end);
            var module = ReadNullableString(end);
            return new MemoryRetentionRoot((MemoryRootKind)kindValue, flags, function, module);
        }

        /// <summary>
        /// 读取以字节长度编码的可空 UTF-8 字符串，防止受损工件声明跨对象或不受限的大字段。
        /// </summary>
        private string? ReadNullableString(long end)
        {
            if (end - _evidenceStream.Position < sizeof(int))
            {
                throw new InvalidDataException("根证据字符串长度截断。");
            }

            var length = _evidence.ReadInt32();
            if (length == -1)
            {
                return null;
            }

            if (length < 0 || length > MaximumEvidenceRecordBytes || length > end - _evidenceStream.Position)
            {
                throw new InvalidDataException("根证据字符串长度无效。");
            }

            var bytes = _evidence.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new InvalidDataException("根证据字符串截断。");
            }

            return Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>
    /// 获取或启动当前映射索引的共享派生构建；基础索引查询不等待该任务，调用方取消也不会取消共享构建。
    /// </summary>
    private Task<HeapDerivedAnalysisArtifact> GetOrStartDerivedAnalysis()
    {
        lock (_derivedAnalysisSync)
        {
            if (_derivedAnalysisTask is { IsCompleted: true, IsCompletedSuccessfully: false })
            {
                _derivedAnalysisTask = null;
            }

            return _derivedAnalysisTask ??= Task.Run(
                () => HeapDerivedAnalysisArtifact.OpenOrBuild(_directory, _disposeCancellationSource.Token),
                CancellationToken.None);
        }
    }
}
