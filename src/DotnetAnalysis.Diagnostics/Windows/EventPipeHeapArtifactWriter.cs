using DotnetAnalysis.Core.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 把已冻结的 EventPipe 固定宽度 spool 转换为 v3 基础索引工件；所有对象地址与边映射均通过受控外排排序和顺序归并完成。
/// </summary>
internal sealed class EventPipeHeapArtifactWriter
{
    private const int ObjectSpoolRecordBytes = sizeof(ulong) + sizeof(ulong) + sizeof(long) + sizeof(long);
    private const int EdgeSpoolRecordBytes = sizeof(ulong);
    private const int RootSpoolRecordBytes = sizeof(ulong) + sizeof(int);

    private static readonly string[] s_artifactNames =
    [
        "types.bin",
        "type-summary.bin",
        "objects.bin",
        "address-to-id.bin",
        "objects-by-type.bin",
        "forward-offsets.bin",
        "forward-targets.bin",
        "reverse-offsets.bin",
        "reverse-targets.bin",
        "roots-by-object.bin",
        "root-evidence.bin"
    ];

    private readonly string _directory;
    private readonly string _objectSpoolPath;
    private readonly string _edgeSpoolPath;
    private readonly string _rootSpoolPath;
    private readonly int _objectCount;
    private readonly long _edgeCount;
    private readonly long _rootCount;
    private readonly IReadOnlyDictionary<ulong, TypeIdentity> _knownTypes;

    /// <summary>
    /// 创建一次只读 spool 到索引工件的投影器；输入生命周期由调用方管理，输出只写入发布临时目录。
    /// </summary>
    /// <param name="directory">原子发布器提供的临时目录。</param>
    /// <param name="objectSpoolPath">固定宽度对象记录路径。</param>
    /// <param name="edgeSpoolPath">按节点槽位顺序排列的目标地址路径。</param>
    /// <param name="rootSpoolPath">GC 根对象地址路径。</param>
    /// <param name="objectCount">有效对象记录数。</param>
    /// <param name="edgeCount">目标地址记录数。</param>
    /// <param name="rootCount">非零根地址记录数。</param>
    /// <param name="knownTypes">最终收到的类型标识表，规模仅随类型数增长。</param>
    public EventPipeHeapArtifactWriter(
        string directory,
        string objectSpoolPath,
        string edgeSpoolPath,
        string rootSpoolPath,
        int objectCount,
        long edgeCount,
        long rootCount,
        IReadOnlyDictionary<ulong, TypeIdentity> knownTypes)
    {
        _directory = directory;
        _objectSpoolPath = objectSpoolPath;
        _edgeSpoolPath = edgeSpoolPath;
        _rootSpoolPath = rootSpoolPath;
        _objectCount = objectCount;
        _edgeCount = edgeCount;
        _rootCount = rootCount;
        _knownTypes = knownTypes;
    }

    /// <summary>
    /// 顺序生成类型、对象、地址、分页、正反向 CSR 与未知根证据工件，并删除所有中间外排文件。
    /// </summary>
    /// <param name="cancellationToken">取消当前构建。</param>
    /// <returns>供 v3 清单严格校验的固定工件名集合。</returns>
    public async Task<IReadOnlyList<string>> WriteAsync(CancellationToken cancellationToken)
    {
        ValidateInputLengths();
        var catalog = BuildTypeCatalog(cancellationToken);
        await WriteTypesAsync(catalog, cancellationToken).ConfigureAwait(false);
        await WriteTypeSummaryAsync(catalog, cancellationToken).ConfigureAwait(false);

        var addressUnsortedPath = Path.Combine(_directory, "address-to-id.unsorted.bin");
        var typeObjectUnsortedPath = Path.Combine(_directory, "objects-by-type.unsorted.bin");
        await WriteObjectsAsync(catalog, addressUnsortedPath, typeObjectUnsortedPath, cancellationToken).ConfigureAwait(false);

        var chunkBytes = HeapIndexResourcePolicy.GetExternalSortChunkBytes();
        var addressPath = Path.Combine(_directory, "address-to-id.bin");
        await HeapIndexExternalSorter.SortAddressIdRecordsAsync(
            addressUnsortedPath,
            addressPath,
            _directory,
            chunkBytes,
            cancellationToken).ConfigureAwait(false);
        await Task.Run(
            () => ValidateAddressIndex(addressPath, cancellationToken),
            CancellationToken.None).ConfigureAwait(false);
        File.Delete(addressUnsortedPath);

        var typeObjectSortedPath = Path.Combine(_directory, "objects-by-type.sorted.bin");
        await HeapIndexExternalSorter.SortObjectIdEdgeRecordsAsync(
            typeObjectUnsortedPath,
            typeObjectSortedPath,
            _directory,
            chunkBytes,
            sortByTarget: false,
            cancellationToken).ConfigureAwait(false);
        File.Delete(typeObjectUnsortedPath);
        await WriteObjectsByTypeAsync(typeObjectSortedPath, cancellationToken).ConfigureAwait(false);
        File.Delete(typeObjectSortedPath);

        await WriteCsrArtifactsAsync(addressPath, chunkBytes, cancellationToken).ConfigureAwait(false);
        await WriteRootArtifactsAsync(addressPath, chunkBytes, cancellationToken).ConfigureAwait(false);
        return s_artifactNames;
    }

    /// <summary>
    /// 验证输入文件长度与调用方冻结时记录的计数严格一致，避免截断或尾随数据生成部分图。
    /// </summary>
    private void ValidateInputLengths()
    {
        if (new FileInfo(_objectSpoolPath).Length != checked((long)_objectCount * ObjectSpoolRecordBytes)
            || new FileInfo(_edgeSpoolPath).Length != checked(_edgeCount * EdgeSpoolRecordBytes)
            || new FileInfo(_rootSpoolPath).Length != checked(_rootCount * RootSpoolRecordBytes))
        {
            throw new InvalidDataException("EventPipe 固定宽度 spool 长度无效。");
        }
    }

    /// <summary>
    /// 扫描对象记录，以实际使用的运行时类型标识聚合数量和浅表大小，再按类型身份建立稳定索引；工作集仅随类型数增长。
    /// </summary>
    private EventPipeTypeCatalog BuildTypeCatalog(CancellationToken cancellationToken)
    {
        var aggregatesByRuntimeId = new Dictionary<ulong, TypeAggregate>();
        using (var objects = OpenReader(_objectSpoolPath))
        {
            for (var objectId = 0; objectId < _objectCount; objectId++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = ReadObject(objects);
                aggregatesByRuntimeId.TryGetValue(row.TypeId, out var aggregate);
                aggregatesByRuntimeId[row.TypeId] = new TypeAggregate(
                    checked(aggregate.Count + 1),
                    checked(aggregate.SizeBytes + row.SizeBytes));
            }
        }

        var types = aggregatesByRuntimeId.Keys
            .Select(ResolveType)
            .Distinct()
            .OrderBy(static type => type.TypeName, StringComparer.Ordinal)
            .ThenBy(static type => type.AssemblyName, StringComparer.Ordinal)
            .ToArray();
        var indexByType = types
            .Select(static (type, index) => (type, index))
            .ToDictionary(static pair => pair.type, static pair => pair.index);
        var indexByRuntimeId = new Dictionary<ulong, int>(aggregatesByRuntimeId.Count);
        var counts = new long[types.Length];
        var sizes = new long[types.Length];
        foreach (var pair in aggregatesByRuntimeId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var typeIndex = indexByType[ResolveType(pair.Key)];
            indexByRuntimeId.Add(pair.Key, typeIndex);
            counts[typeIndex] = checked(counts[typeIndex] + pair.Value.Count);
            sizes[typeIndex] = checked(sizes[typeIndex] + pair.Value.SizeBytes);
        }

        return new EventPipeTypeCatalog(types, indexByRuntimeId, counts, sizes);
    }

    /// <summary>
    /// 按稳定类型索引写入紧凑类型表；程序集缺失时保留空字符串格式约定。
    /// </summary>
    private async Task WriteTypesAsync(EventPipeTypeCatalog catalog, CancellationToken cancellationToken)
    {
        await using var stream = CreateOutput("types.bin");
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(catalog.Types.Length);
        foreach (var type in catalog.Types)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Write(type.TypeName);
            writer.Write(type.AssemblyName ?? string.Empty);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 写入按总浅表大小降序、类型名升序排列的类型统计，保持内存索引公开排序语义。
    /// </summary>
    private async Task WriteTypeSummaryAsync(EventPipeTypeCatalog catalog, CancellationToken cancellationToken)
    {
        var summaries = catalog.Types
            .Select((type, index) => new MemoryTypeSummary(type, catalog.Counts[index], catalog.Sizes[index]))
            .OrderByDescending(static summary => summary.TotalSizeBytes)
            .ThenBy(static summary => summary.Type.TypeName, StringComparer.Ordinal)
            .ToArray();
        await using var stream = CreateOutput("type-summary.bin");
        await JsonSerializer.SerializeAsync(stream, summaries, cancellationToken: cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 顺序写出致密对象行，并同步生成地址和按类型分页的固定宽度外排输入；不保留对象元数据集合。
    /// </summary>
    private async Task WriteObjectsAsync(
        EventPipeTypeCatalog catalog,
        string addressUnsortedPath,
        string typeObjectUnsortedPath,
        CancellationToken cancellationToken)
    {
        using var input = OpenReader(_objectSpoolPath);
        await using var objectStream = CreateOutput("objects.bin");
        await using var addressStream = CreateTemporaryOutput(addressUnsortedPath);
        await using var typeObjectStream = CreateTemporaryOutput(typeObjectUnsortedPath);
        using var objects = new BinaryWriter(objectStream, Encoding.UTF8, leaveOpen: true);
        using var addresses = new BinaryWriter(addressStream, Encoding.UTF8, leaveOpen: true);
        using var typeObjects = new BinaryWriter(typeObjectStream, Encoding.UTF8, leaveOpen: true);
        for (var objectId = 0; objectId < _objectCount; objectId++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = ReadObject(input);
            var typeIndex = catalog.IndexByRuntimeId[row.TypeId];
            objects.Write(row.Address);
            objects.Write(typeIndex);
            objects.Write(row.SizeBytes);
            addresses.Write(row.Address);
            addresses.Write(objectId);
            typeObjects.Write(typeIndex);
            typeObjects.Write(objectId);
        }

        await objectStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await addressStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await typeObjectStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 从按 typeIndex/objectId 排序的中间文件只复制 objectId，形成分页定位工件。
    /// </summary>
    private async Task WriteObjectsByTypeAsync(string sortedPath, CancellationToken cancellationToken)
    {
        using var input = OpenReader(sortedPath);
        await using var stream = CreateOutput("objects-by-type.bin");
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        while (input.BaseStream.Position < input.BaseStream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = input.ReadInt32();
            writer.Write(input.ReadInt32());
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 将节点顺序边槽位恢复为地址对，经两次外排归并转换成 objectId 边，再生成正反向 CSR。
    /// </summary>
    private async Task WriteCsrArtifactsAsync(
        string addressIndexPath,
        int chunkBytes,
        CancellationToken cancellationToken)
    {
        var addressPairsPath = Path.Combine(_directory, "edges-address.unsorted.bin");
        var sourceSortedPath = Path.Combine(_directory, "edges-address.source-sorted.bin");
        var targetWithSourcePath = Path.Combine(_directory, "edges-target.source-id.unsorted.bin");
        var targetSortedPath = Path.Combine(_directory, "edges-target.source-id.sorted.bin");
        var objectIdEdgesPath = Path.Combine(_directory, "edges.object-id.unsorted.bin");
        var forwardPath = Path.Combine(_directory, "edges.forward.bin");
        var reversePath = Path.Combine(_directory, "edges.reverse.bin");
        try
        {
            await WriteAddressEdgesAsync(addressPairsPath, cancellationToken).ConfigureAwait(false);
            await HeapIndexExternalSorter.SortAddressPairRecordsAsync(
                addressPairsPath,
                sourceSortedPath,
                _directory,
                chunkBytes,
                sortBySecondAddress: false,
                cancellationToken).ConfigureAwait(false);
            await WriteTargetAddressesWithSourceIdsAsync(
                sourceSortedPath,
                addressIndexPath,
                targetWithSourcePath,
                cancellationToken).ConfigureAwait(false);
            await HeapIndexExternalSorter.SortAddressIdRecordsAsync(
                targetWithSourcePath,
                targetSortedPath,
                _directory,
                chunkBytes,
                cancellationToken).ConfigureAwait(false);
            await WriteObjectIdEdgesAsync(targetSortedPath, addressIndexPath, objectIdEdgesPath, cancellationToken).ConfigureAwait(false);
            await HeapIndexExternalSorter.SortObjectIdEdgeRecordsAsync(
                objectIdEdgesPath,
                forwardPath,
                _directory,
                chunkBytes,
                sortByTarget: false,
                cancellationToken).ConfigureAwait(false);
            await HeapIndexExternalSorter.SortObjectIdEdgeRecordsAsync(
                objectIdEdgesPath,
                reversePath,
                _directory,
                chunkBytes,
                sortByTarget: true,
                cancellationToken).ConfigureAwait(false);
            await Task.Run(
                () => HeapIndexArtifactStore.WriteCsrFromSortedEdges(_directory, "forward", forwardPath, _objectCount, sortByTarget: false, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
            await Task.Run(
                () => HeapIndexArtifactStore.WriteCsrFromSortedEdges(_directory, "reverse", reversePath, _objectCount, sortByTarget: true, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            HeapTemporaryArtifactCleanup.TryDeleteFile(addressPairsPath);
            HeapTemporaryArtifactCleanup.TryDeleteFile(sourceSortedPath);
            HeapTemporaryArtifactCleanup.TryDeleteFile(targetWithSourcePath);
            HeapTemporaryArtifactCleanup.TryDeleteFile(targetSortedPath);
            HeapTemporaryArtifactCleanup.TryDeleteFile(objectIdEdgesPath);
            HeapTemporaryArtifactCleanup.TryDeleteFile(forwardPath);
            HeapTemporaryArtifactCleanup.TryDeleteFile(reversePath);
        }
    }

    /// <summary>
    /// 顺序消费每个节点声明的目标槽位并写出源/目标地址对；零目标只占槽位而不形成引用边。
    /// </summary>
    private async Task WriteAddressEdgesAsync(string outputPath, CancellationToken cancellationToken)
    {
        using var objects = OpenReader(_objectSpoolPath);
        using var targets = OpenReader(_edgeSpoolPath);
        await using var stream = CreateTemporaryOutput(outputPath);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        long consumedEdges = 0;
        for (var objectId = 0; objectId < _objectCount; objectId++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = ReadObject(objects);
            for (long edgeIndex = 0; edgeIndex < row.EdgeCount; edgeIndex++)
            {
                var targetAddress = targets.ReadUInt64();
                consumedEdges++;
                if (targetAddress != 0)
                {
                    writer.Write(row.Address);
                    writer.Write(targetAddress);
                }
            }
        }

        if (consumedEdges != _edgeCount || targets.BaseStream.Position != targets.BaseStream.Length)
        {
            throw new InvalidDataException("EventPipe 边槽位数量与边 spool 不一致。");
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 将按源地址排序的地址边与地址索引顺序归并，写出 targetAddress/sourceObjectId 记录。
    /// </summary>
    private static async Task WriteTargetAddressesWithSourceIdsAsync(
        string sourceSortedPath,
        string addressIndexPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        using var pairs = OpenReader(sourceSortedPath);
        using var addresses = OpenReader(addressIndexPath);
        await using var stream = CreateTemporaryOutput(outputPath);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        var hasAddress = TryReadAddressId(addresses, out var currentAddress, out var currentObjectId);
        while (TryReadAddressPair(pairs, out var sourceAddress, out var targetAddress))
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (hasAddress && currentAddress < sourceAddress)
            {
                hasAddress = TryReadAddressId(addresses, out currentAddress, out currentObjectId);
            }

            if (hasAddress && currentAddress == sourceAddress)
            {
                writer.Write(targetAddress);
                writer.Write(currentObjectId);
                continue;
            }

            throw new InvalidDataException("EventPipe 引用边包含未知非零源地址。");
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 将按目标地址排序的 targetAddress/sourceObjectId 与地址索引顺序归并为 sourceObjectId/targetObjectId。
    /// </summary>
    private static async Task WriteObjectIdEdgesAsync(
        string targetSortedPath,
        string addressIndexPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        using var sources = OpenReader(targetSortedPath);
        using var addresses = OpenReader(addressIndexPath);
        await using var stream = CreateTemporaryOutput(outputPath);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        var hasAddress = TryReadAddressId(addresses, out var currentAddress, out var currentObjectId);
        while (TryReadAddressId(sources, out var targetAddress, out var sourceObjectId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (hasAddress && currentAddress < targetAddress)
            {
                hasAddress = TryReadAddressId(addresses, out currentAddress, out currentObjectId);
            }

            if (hasAddress && currentAddress == targetAddress)
            {
                writer.Write(sourceObjectId);
                writer.Write(currentObjectId);
                continue;
            }

            throw new InvalidDataException("EventPipe 引用边包含未知非零目标地址。");
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 将根地址外排后与地址索引顺序归并，并生成按 objectId 偏移的单条 Unknown 证据。
    /// </summary>
    private async Task WriteRootArtifactsAsync(
        string addressIndexPath,
        int chunkBytes,
        CancellationToken cancellationToken)
    {
        var rootsUnsortedPath = Path.Combine(_directory, "roots-address.unsorted.bin");
        var rootsSortedPath = Path.Combine(_directory, "roots-address.sorted.bin");
        var rootObjectIdsPath = Path.Combine(_directory, "roots-object-id.unsorted.bin");
        var sortedObjectIdsPath = Path.Combine(_directory, "roots-object-id.sorted.bin");
        try
        {
            await WriteRootAddressRecordsAsync(rootsUnsortedPath, cancellationToken).ConfigureAwait(false);
            await HeapIndexExternalSorter.SortAddressIdRecordsAsync(
                rootsUnsortedPath,
                rootsSortedPath,
                _directory,
                chunkBytes,
                cancellationToken).ConfigureAwait(false);
            await WriteRootObjectIdsAsync(rootsSortedPath, addressIndexPath, rootObjectIdsPath, cancellationToken).ConfigureAwait(false);
            await HeapIndexExternalSorter.SortObjectIdEdgeRecordsAsync(
                rootObjectIdsPath,
                sortedObjectIdsPath,
                _directory,
                chunkBytes,
                sortByTarget: false,
                cancellationToken).ConfigureAwait(false);
            await WriteUnknownRootEvidenceAsync(sortedObjectIdsPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            HeapTemporaryArtifactCleanup.TryDeleteFile(rootsUnsortedPath);
            HeapTemporaryArtifactCleanup.TryDeleteFile(rootsSortedPath);
            HeapTemporaryArtifactCleanup.TryDeleteFile(rootObjectIdsPath);
            HeapTemporaryArtifactCleanup.TryDeleteFile(sortedObjectIdsPath);
        }
    }

    /// <summary>
    /// 将每个根写成 address/ordinal 固定宽度记录；ordinal 只用于满足通用地址外排格式。
    /// </summary>
    private async Task WriteRootAddressRecordsAsync(string outputPath, CancellationToken cancellationToken)
    {
        using var roots = OpenReader(_rootSpoolPath);
        await using var stream = CreateTemporaryOutput(outputPath);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        for (long rootIndex = 0; rootIndex < _rootCount; rootIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Write(roots.ReadUInt64());
            writer.Write(roots.ReadInt32());
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 顺序归并根地址和对象地址，输出 objectId/零占位符；无对应对象的根被稳定忽略。
    /// </summary>
    private static async Task WriteRootObjectIdsAsync(
        string rootsSortedPath,
        string addressIndexPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        using var roots = OpenReader(rootsSortedPath);
        using var addresses = OpenReader(addressIndexPath);
        await using var stream = CreateTemporaryOutput(outputPath);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        var hasAddress = TryReadAddressId(addresses, out var currentAddress, out var currentObjectId);
        while (TryReadAddressId(roots, out var rootAddress, out var rawFlags))
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (hasAddress && currentAddress < rootAddress)
            {
                hasAddress = TryReadAddressId(addresses, out currentAddress, out currentObjectId);
            }

            if (hasAddress && currentAddress == rootAddress)
            {
                writer.Write(currentObjectId);
                writer.Write(rawFlags);
            }
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 逐对象写入根证据偏移；相同根对象的重复 EventPipe 记录只保留一条 Unknown 证据。
    /// </summary>
    private async Task WriteUnknownRootEvidenceAsync(string sortedObjectIdsPath, CancellationToken cancellationToken)
    {
        using var roots = OpenReader(sortedObjectIdsPath);
        await using var offsetStream = CreateOutput("roots-by-object.bin");
        await using var evidenceStream = CreateOutput("root-evidence.bin");
        using var offsets = new BinaryWriter(offsetStream, Encoding.UTF8, leaveOpen: true);
        using var evidence = new BinaryWriter(evidenceStream, Encoding.UTF8, leaveOpen: true);
        var hasRoot = TryReadObjectIdPair(roots, out var pendingObjectId, out var pendingFlags);
        for (var objectId = 0; objectId < _objectCount; objectId++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            offsets.Write(evidenceStream.Position);
            if (hasRoot && pendingObjectId == objectId)
            {
                int? previousFlags = null;
                do
                {
                    if (pendingFlags is not 0 and not (int)MemoryRootFlags.WeakReference)
                    {
                        throw new InvalidDataException("EventPipe 根记录包含不支持的标志。");
                    }

                    if (previousFlags != pendingFlags)
                    {
                        WriteUnknownRoot(evidence, (MemoryRootFlags)pendingFlags);
                        previousFlags = pendingFlags;
                    }

                    hasRoot = TryReadObjectIdPair(roots, out pendingObjectId, out pendingFlags);
                }
                while (hasRoot && pendingObjectId == objectId);
            }
        }

        if (hasRoot)
        {
            throw new InvalidDataException("EventPipe 根对象标识超出对象范围。");
        }

        offsets.Write(evidenceStream.Position);
        await offsetStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await evidenceStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 写入不含函数或模块声明的未知根，避免将 EventPipe GCBulkRootEdge 误表述为栈证据。
    /// </summary>
    private static void WriteUnknownRoot(BinaryWriter writer, MemoryRootFlags flags)
    {
        writer.Write((byte)MemoryRootKind.Unknown);
        writer.Write((int)flags);
        writer.Write(-1);
        writer.Write(-1);
    }

    /// <summary>
    /// 解析运行时类型标识；缺少 TypeBulkType 记录时返回稳定占位身份。
    /// </summary>
    private TypeIdentity ResolveType(ulong typeId) => _knownTypes.TryGetValue(typeId, out var type)
        ? type
        : new TypeIdentity($"Type(0x{typeId:x})", null);

    /// <summary>
    /// 打开一个固定宽度输入并启用顺序扫描提示。
    /// </summary>
    private static BinaryReader OpenReader(string path) => new(
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan),
        Encoding.UTF8,
        leaveOpen: false);

    /// <summary>
    /// 创建最终命名工件输出；发布临时目录中禁止覆盖已有文件。
    /// </summary>
    private FileStream CreateOutput(string fileName) => CreateTemporaryOutput(Path.Combine(_directory, fileName));

    /// <summary>
    /// 创建顺序写入的中间文件，异步刷新由调用方在阶段边界执行。
    /// </summary>
    private static FileStream CreateTemporaryOutput(string path) => new(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>
    /// 从对象 spool 读取一条固定宽度记录。
    /// </summary>
    private static EventPipeObjectRecord ReadObject(BinaryReader reader)
    {
        var record = new EventPipeObjectRecord(
            reader.ReadUInt64(),
            reader.ReadUInt64(),
            reader.ReadInt64(),
            reader.ReadInt64());
        if (record.SizeBytes < 0 || record.EdgeCount < 0)
        {
            throw new InvalidDataException("EventPipe 对象记录包含负大小或负边槽位数量。");
        }

        return record;
    }

    /// <summary>
    /// 从固定宽度地址对输入读取下一条记录；EOF 只能出现在记录边界。
    /// </summary>
    private static bool TryReadAddressPair(BinaryReader reader, out ulong firstAddress, out ulong secondAddress)
    {
        if (reader.BaseStream.Position == reader.BaseStream.Length)
        {
            firstAddress = default;
            secondAddress = default;
            return false;
        }

        if (reader.BaseStream.Length - reader.BaseStream.Position < sizeof(ulong) * 2)
        {
            throw new InvalidDataException("EventPipe 地址边记录截断。");
        }

        firstAddress = reader.ReadUInt64();
        secondAddress = reader.ReadUInt64();
        return true;
    }

    /// <summary>
    /// 从 address/int32 输入读取下一条记录；该格式同时用于地址索引和目标映射中间文件。
    /// </summary>
    private static bool TryReadAddressId(BinaryReader reader, out ulong address, out int objectId)
    {
        if (reader.BaseStream.Position == reader.BaseStream.Length)
        {
            address = default;
            objectId = default;
            return false;
        }

        if (reader.BaseStream.Length - reader.BaseStream.Position < sizeof(ulong) + sizeof(int))
        {
            throw new InvalidDataException("EventPipe 地址对象标识记录截断。");
        }

        address = reader.ReadUInt64();
        objectId = reader.ReadInt32();
        return true;
    }

    /// <summary>
    /// 顺序验证外排地址索引严格递增且 objectId 位于基础对象范围；只保留上一条记录，不创建全量去重集合。
    /// </summary>
    /// <param name="path">已经按地址和 objectId 排序的固定宽度索引。</param>
    /// <param name="cancellationToken">取消当前完整性扫描。</param>
    private void ValidateAddressIndex(string path, CancellationToken cancellationToken)
    {
        using var reader = OpenReader(path);
        var hasPrevious = false;
        ulong previousAddress = 0;
        var recordCount = 0;
        while (TryReadAddressId(reader, out var address, out var objectId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (objectId < 0 || objectId >= _objectCount)
            {
                throw new InvalidDataException("EventPipe 地址索引包含越界 objectId。");
            }

            if (address == 0)
            {
                throw new InvalidDataException("EventPipe 对象地址不得为零。");
            }

            if (hasPrevious && address <= previousAddress)
            {
                throw new InvalidDataException("EventPipe 对象地址必须唯一且严格递增。");
            }

            hasPrevious = true;
            previousAddress = address;
            recordCount = checked(recordCount + 1);
        }

        if (recordCount != _objectCount)
        {
            throw new InvalidDataException("EventPipe 地址索引记录数与对象数量不一致。");
        }
    }

    /// <summary>
    /// 从 objectId/objectId 输入读取下一条记录；根排序文件复用通用边记录布局。
    /// </summary>
    private static bool TryReadObjectIdPair(BinaryReader reader, out int first, out int second)
    {
        if (reader.BaseStream.Position == reader.BaseStream.Length)
        {
            first = default;
            second = default;
            return false;
        }

        if (reader.BaseStream.Length - reader.BaseStream.Position < sizeof(int) * 2)
        {
            throw new InvalidDataException("EventPipe 对象标识对记录截断。");
        }

        first = reader.ReadInt32();
        second = reader.ReadInt32();
        return true;
    }

    /// <summary>
    /// 表示对象 spool 的单条固定宽度记录。
    /// </summary>
    private readonly record struct EventPipeObjectRecord(ulong Address, ulong TypeId, long SizeBytes, long EdgeCount);

    /// <summary>
    /// 表示单个运行时类型标识对应的对象数量与浅表大小统计。
    /// </summary>
    private readonly record struct TypeAggregate(long Count, long SizeBytes);

    /// <summary>
    /// 保存类型数量级的稳定类型表、运行时 ID 映射与聚合值；其生命周期仅覆盖一次索引构建。
    /// </summary>
    private sealed record EventPipeTypeCatalog(
        TypeIdentity[] Types,
        IReadOnlyDictionary<ulong, int> IndexByRuntimeId,
        long[] Counts,
        long[] Sizes);
}
