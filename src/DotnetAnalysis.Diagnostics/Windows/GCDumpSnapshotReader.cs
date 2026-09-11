using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 读取 FastSerialization 或 EventPipe .gcdump 并提供统一堆查询。
/// </summary>
public sealed class GCDumpSnapshotReader : IIndexedMemorySnapshotReader
{
    /// <summary>
    /// 判断文件是否为 FastSerialization 或 EventPipe 形式的 .gcdump。
    /// </summary>
    public bool CanRead(string filePath) => string.Equals(Path.GetExtension(filePath), ".gcdump", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 异步读取快照的按类型对象统计。
    /// </summary>
    public Task<IReadOnlyList<MemoryTypeSummary>> ReadTypeSummariesAsync(string filePath, CancellationToken cancellationToken)
    {
        Validate(filePath, cancellationToken);
        return ReadTypeSummariesCoreAsync(filePath, cancellationToken);
    }

    /// <summary>
    /// 异步读取指定类型的全部对象；大型快照应改用分页分析服务。
    /// </summary>
    /// <exception cref="DiagnosticsException">对象数达到 100,000 时，以 <see cref="DiagnosticsErrorCode.SnapshotTooLargeForFullEnumeration"/> 引发。</exception>
    public Task<IReadOnlyList<MemoryObjectInfo>> ReadObjectsAsync(string filePath, TypeIdentity type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(type);
        Validate(filePath, cancellationToken);
        return ReadObjectsCoreAsync(filePath, type, cancellationToken);
    }

    /// <summary>
    /// 异步计算到指定对象地址的引用路径。
    /// </summary>
    public Task<MemoryReferencePath?> ReadReferencePathAsync(string filePath, ulong objectAddress, CancellationToken cancellationToken)
    {
        Validate(filePath, cancellationToken);
        return ReadReferencePathCoreAsync(filePath, objectAddress, cancellationToken);
    }

    /// <summary>
    /// 验证路径存在、非空且调用未被取消。
    /// </summary>
    private static void Validate(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.CaptureFailed, "Snapshot file does not exist.");
        }

        if (new FileInfo(filePath).Length == 0)
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.CaptureFailed, "Snapshot file is empty.");
        }
    }

    /// <summary>
    /// 解析 FastSerialization 或原始 EventPipe 流为统一堆模型。
    /// </summary>
    private static HeapData ReadHeap(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryReadFastSerializationGcdump(filePath, cancellationToken, out var serializedHeap))
        {
            return serializedHeap;
        }

        try
        {
            using var source = new EventPipeEventSource(filePath);
            var builder = new EventPipeHeapBuilder();
            builder.Attach(source);
            source.Process();
            return builder.Build();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or IOException
                or EndOfStreamException
                or ArgumentException
                or InvalidDataException
                or OverflowException
                or FormatException)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.CaptureFailed,
                "The gcdump could not be parsed.",
                exception);
        }

    }

    /// <summary>
    /// 供捕获写入器读取统一堆模型的内部入口。
    /// </summary>
    internal static HeapData ReadHeapForSerialization(
        string filePath,
        CancellationToken cancellationToken) =>
        ReadHeap(filePath, cancellationToken);

    /// <inheritdoc />
    Task<HeapIndexHandle> IIndexedMemorySnapshotReader.BuildIndexAsync(
        MemorySnapshotId snapshotId,
        string filePath,
        HeapIndexRouter router,
        CancellationToken cancellationToken) =>
        BuildIndexCoreAsync(snapshotId, filePath, router, cancellationToken);

    /// <summary>
    /// 建立由路由器选择的索引句柄。FastSerialization 大图直接流式生成磁盘工件，避免节点标签、节点 blob、地址表和边同时驻留托管内存。
    /// </summary>
    private static async Task<HeapIndexHandle> BuildIndexCoreAsync(
        MemorySnapshotId snapshotId,
        string filePath,
        HeapIndexRouter router,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(router);
        var existing = await router
            .TryOpenExistingMappedAsync(snapshotId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        Validate(filePath, cancellationToken);
        if (TryReadFastSerializationCounts(filePath, out var counts))
        {
            if (router.ShouldUseMapped(counts.AddressCount, counts.EstimatedEdgeCount))
            {
                return await router.BuildMappedAsync(
                    snapshotId,
                    (directory, token) => HeapIndexArtifactStore.PublishAsync(
                        directory,
                        (temporaryDirectory, writeToken) => WriteFastSerializationMappedArtifactsAsync(temporaryDirectory, filePath, writeToken),
                        HeapArtifactStorageGuard.EstimateIndexBuildPeakBytes(
                            counts.AddressCount,
                            counts.EstimatedEdgeCount,
                            new FileInfo(filePath).Length),
                        token),
                    cancellationToken).ConfigureAwait(false);
            }

            var indexTask = Task.Run(
                () => ReadIndex(filePath, CancellationToken.None),
                CancellationToken.None);
            var index = await indexTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await router.RouteAsync(snapshotId, index, cancellationToken).ConfigureAwait(false);
        }

        return await BuildEventPipeIndexAsync(snapshotId, filePath, router, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 将非 FastSerialization EventPipe 文件流式写入受管 spool，并在实际计数可用后自动路由；
    /// 原始 .gcdump 始终只读，本次工作区在成功、取消或异常时都会删除。
    /// </summary>
    private static async Task<HeapIndexHandle> BuildEventPipeIndexAsync(
        MemorySnapshotId snapshotId,
        string filePath,
        HeapIndexRouter router,
        CancellationToken cancellationToken)
    {
        var indexDirectory = router.GetHeapIndexDirectory(snapshotId);
        var snapshotDirectory = Path.GetDirectoryName(indexDirectory)
            ?? throw new InvalidDataException("堆索引目录缺少快照父目录。");
        Directory.CreateDirectory(snapshotDirectory);
        var sourceLength = new FileInfo(filePath).Length;
        var storageGuard = HeapArtifactStorageGuard.CreateForTargetDirectory(indexDirectory);
        using var spoolReservation = await storageGuard
            .ReservePublicationAsync(
                indexDirectory,
                HeapArtifactStorageGuard.EstimateIndexBuildPeakBytes(0, 0, sourceLength),
                cancellationToken)
            .ConfigureAwait(false);
        using var spool = await EventPipeHeapSpool.CreateAsync(snapshotDirectory, cancellationToken).ConfigureAwait(false);
        await Task.Run(
            () => ReadEventPipeIntoSpool(filePath, spool, cancellationToken),
            CancellationToken.None).ConfigureAwait(false);
        await spoolReservation
            .ReconcileActualUsageAsync(spool.WorkingDirectory, cancellationToken)
            .ConfigureAwait(false);
        return await RouteEventPipeSpoolAsync(
            snapshotId,
            spool,
            sourceLength,
            router,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按 EventPipe 解析完成后的实际对象和边计数选择内存或磁盘索引；大图写入回调直接消费 spool，
    /// 不先构造 <see cref="SnapshotIndex"/>。
    /// </summary>
    /// <param name="snapshotId">待发布索引所属快照。</param>
    /// <param name="spool">已完整消费 EventPipe 文件的固定宽度 spool。</param>
    /// <param name="sourceLengthBytes">原始 EventPipe 文件长度，用于保守估算类型元数据与外排峰值。</param>
    /// <param name="router">Diagnostics 内部资源路由器。</param>
    /// <param name="cancellationToken">取消当前共享构建。</param>
    /// <returns>内存或映射实现均隐藏在统一句柄后的可查询索引。</returns>
    internal static async Task<HeapIndexHandle> RouteEventPipeSpoolAsync(
        MemorySnapshotId snapshotId,
        EventPipeHeapSpool spool,
        long sourceLengthBytes,
        HeapIndexRouter router,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLengthBytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (!router.ShouldUseMapped(spool.ObjectCount, spool.EdgeCount))
        {
            return HeapIndexHandle.CreateInMemory(spool.BuildInMemoryIndex(cancellationToken));
        }

        return await router.BuildMappedAsync(
            snapshotId,
            (directory, token) => HeapIndexArtifactStore.PublishAsync(
                directory,
                spool.WriteMappedArtifactsAsync,
                HeapArtifactStorageGuard.EstimateIndexBuildPeakBytes(
                    spool.ObjectCount,
                    spool.EdgeCount,
                    sourceLengthBytes),
                token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 驱动 EventPipe 文件解析并立即写入固定宽度记录；取消会停止 TraceEvent 消费并在返回前重新抛出。
    /// </summary>
    private static void ReadEventPipeIntoSpool(
        string filePath,
        EventPipeHeapSpool spool,
        CancellationToken cancellationToken)
    {
        try
        {
            using var source = new EventPipeEventSource(filePath);
            spool.Attach(source);
            using var cancellationRegistration = cancellationToken.Register(source.StopProcessing);
            source.Process();
            cancellationToken.ThrowIfCancellationRequested();
            if (spool.ObjectCount == 0)
            {
                throw new DiagnosticsException(
                    DiagnosticsErrorCode.CaptureFailed,
                    "The gcdump did not contain heap object events.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DiagnosticsException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or IOException
                or EndOfStreamException
                or ArgumentException
                or UnauthorizedAccessException
                or InvalidDataException
                or OverflowException
                or FormatException)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.CaptureFailed,
                "The gcdump could not be parsed.",
                exception);
        }
    }

    /// <summary>
    /// 只扫描 FastSerialization 头部和表长度以获得路由所需的对象、边估算；该预读不保留节点标签、地址或 blob。
    /// </summary>
    /// <param name="filePath">待探测的 .gcdump 路径。</param>
    /// <param name="counts">成功时返回图的已声明计数。</param>
    /// <returns>文件为本读取器支持的 FastSerialization 堆图时返回 <see langword="true"/>。</returns>
    private static bool TryReadFastSerializationCounts(string filePath, out FastSerializationGraphCounts counts)
    {
        counts = default;
        try
        {
            using var reader = new FastSerializationReader(filePath);
            if (reader.ReadInt32() != 20
                || !string.Equals(reader.ReadUtf8(20), "!FastSerialization.1", StringComparison.Ordinal))
            {
                return false;
            }

            var rootType = reader.ReadObjectHeader();
            if (!IsGcHeapDumpType(rootType))
            {
                return false;
            }

            if (!string.Equals(reader.ReadObjectHeader(), "Graphs.MemoryGraph", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Unexpected gcdump graph type.");
            }

            _ = reader.ReadInt64();
            _ = reader.ReadInt32();
            var typeCount = reader.ReadInt32();
            ValidateFastTypeCount(typeCount);
            for (var typeIndex = 0; typeIndex < typeCount; typeIndex++)
            {
                reader.SkipString(nullable: true);
                _ = reader.ReadInt32();
                reader.SkipString(nullable: true);
            }

            var nodeCount = reader.ReadInt32();
            ValidateFastNodeCount(nodeCount);
            reader.SkipBytes(checked((long)nodeCount * sizeof(int)));
            var blobLength = reader.ReadInt32();
            if (blobLength < 0 || blobLength > reader.Remaining)
            {
                throw new InvalidDataException("Invalid gcdump node blob length.");
            }

            reader.SkipBytes(blobLength);
            var addressCount = reader.ReadInt32();
            if (addressCount < 0 || addressCount > nodeCount || checked((long)addressCount * sizeof(long)) > reader.Remaining)
            {
                throw new InvalidDataException("Invalid gcdump address count.");
            }

            // 每条压缩 child 至少占一个 blob 字节，因此 blob 长度既能避免稠密图误走内存路径，
            // 又为磁盘工件预留提供无需完整解析即可确定的边数上界。
            counts = new FastSerializationGraphCounts(addressCount, nodeCount, blobLength);
            return true;
        }
        catch (InvalidDataException exception)
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.CaptureFailed, "The gcdump could not be parsed.", exception);
        }
        catch (EndOfStreamException exception)
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.CaptureFailed, "The gcdump is truncated.", exception);
        }
        catch (IOException exception)
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.CaptureFailed, "The gcdump could not be read.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.CaptureFailed, "The gcdump could not be read.", exception);
        }
    }

    /// <summary>
    /// 通过临时 spool 将 FastSerialization 图投影为磁盘索引工件；工作文件只存在于原子发布的临时目录中。
    /// </summary>
    /// <param name="directory">当前工件发布的临时目录。</param>
    /// <param name="filePath">原始 .gcdump 路径。</param>
    /// <param name="cancellationToken">构建取消令牌。</param>
    /// <returns>纳入基础工件 manifest 的相对文件名。</returns>
    private static async Task<IReadOnlyList<string>> WriteFastSerializationMappedArtifactsAsync(
        string directory,
        string filePath,
        CancellationToken cancellationToken)
    {
        var spool = await TryCreateFastSerializationSpool(directory, filePath, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Expected FastSerialization gcdump input.");
        try
        {
            return await spool.WriteArtifactsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            spool.Dispose();
        }
    }

    /// <summary>
    /// 将 FastSerialization 中的大型原始表按块复制到工作目录，返回仅保存路径和计数的 spool 描述；不会创建标签、blob 或地址的托管数组。
    /// </summary>
    /// <param name="directory">本次原子发布专用的临时目录。</param>
    /// <param name="filePath">原始 .gcdump 路径。</param>
    /// <param name="cancellationToken">复制期间的取消令牌。</param>
    /// <returns>成功时返回可消费的 spool；输入不是 FastSerialization 堆图时返回空。</returns>
    private static async Task<FastSerializationHeapSpool?> TryCreateFastSerializationSpool(
        string directory,
        string filePath,
        CancellationToken cancellationToken)
    {
        var spoolDirectory = Path.Combine(directory, ".fastserialization-spool");
        Directory.CreateDirectory(spoolDirectory);
        try
        {
            using var reader = new FastSerializationReader(filePath);
            if (reader.ReadInt32() != 20
                || !string.Equals(reader.ReadUtf8(20), "!FastSerialization.1", StringComparison.Ordinal))
            {
                HeapTemporaryArtifactCleanup.TryDeleteDirectory(spoolDirectory);
                return null;
            }

            var rootType = reader.ReadObjectHeader();
            if (!IsGcHeapDumpType(rootType))
            {
                HeapTemporaryArtifactCleanup.TryDeleteDirectory(spoolDirectory);
                return null;
            }

            if (!string.Equals(reader.ReadObjectHeader(), "Graphs.MemoryGraph", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Unexpected gcdump graph type.");
            }

            _ = reader.ReadInt64();
            var rootIndex = reader.ReadInt32();
            var sourceTypeCount = reader.ReadInt32();
            ValidateFastTypeCount(sourceTypeCount);
            var typesPath = Path.Combine(directory, "types.bin");
            var typeSizesPath = Path.Combine(spoolDirectory, "type-sizes.bin");
            var typeIndexesPath = Path.Combine(spoolDirectory, "type-indexes.bin");
            var canonicalTypes = new List<TypeIdentity>(sourceTypeCount);
            var canonicalIndexes = new Dictionary<TypeIdentity, int>(sourceTypeCount);
            await using (var typeSizesStream = new FileStream(typeSizesPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var typeIndexesStream = new FileStream(typeIndexesPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var typeSizesWriter = new BinaryWriter(typeSizesStream, Encoding.UTF8, leaveOpen: true))
            using (var typeIndexesWriter = new BinaryWriter(typeIndexesStream, Encoding.UTF8, leaveOpen: true))
            {
                for (var sourceTypeIndex = 0; sourceTypeIndex < sourceTypeCount; sourceTypeIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = reader.ReadString() ?? $"Type(0x{sourceTypeIndex:x})";
                    var size = reader.ReadInt32();
                    var assembly = reader.ReadString();
                    var type = new TypeIdentity(name, assembly);
                    if (!canonicalIndexes.TryGetValue(type, out var canonicalIndex))
                    {
                        canonicalIndex = canonicalTypes.Count;
                        canonicalIndexes.Add(type, canonicalIndex);
                        canonicalTypes.Add(type);
                    }

                    typeSizesWriter.Write(size);
                    typeIndexesWriter.Write(canonicalIndex);
                }

                await typeSizesStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                await typeIndexesStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var typesStream = new FileStream(typesPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var typesWriter = new BinaryWriter(typesStream, Encoding.UTF8, leaveOpen: true))
            {
                typesWriter.Write(canonicalTypes.Count);
                foreach (var type in canonicalTypes)
                {
                    typesWriter.Write(type.TypeName);
                    typesWriter.Write(type.AssemblyName ?? string.Empty);
                }

                await typesStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var nodeCount = reader.ReadInt32();
            ValidateFastNodeCount(nodeCount);
            var labelsPath = Path.Combine(spoolDirectory, "node-labels.bin");
            await using (var labels = new FileStream(labelsPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await reader.CopyBytesToAsync(labels, checked((long)nodeCount * sizeof(int)), cancellationToken).ConfigureAwait(false);
                await labels.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var blobLength = reader.ReadInt32();
            if (blobLength < 0 || blobLength > reader.Remaining)
            {
                throw new InvalidDataException("Invalid gcdump node blob length.");
            }

            var blobPath = Path.Combine(spoolDirectory, "node-blob.bin");
            await using (var blob = new FileStream(blobPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await reader.CopyBytesToAsync(blob, blobLength, cancellationToken).ConfigureAwait(false);
                await blob.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var addressCount = reader.ReadInt32();
            if (addressCount < 0 || addressCount > nodeCount || checked((long)addressCount * sizeof(long)) > reader.Remaining)
            {
                throw new InvalidDataException("Invalid gcdump address count.");
            }

            var addressesPath = Path.Combine(spoolDirectory, "addresses.bin");
            await using (var addresses = new FileStream(addressesPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await reader.CopyBytesToAsync(addresses, checked((long)addressCount * sizeof(long)), cancellationToken).ConfigureAwait(false);
                await addresses.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!reader.TryReadTaggedByte(out _))
            {
                reader.ExpectTag(FastTag.EndObject);
            }

            return new FastSerializationHeapSpool(
                directory,
                spoolDirectory,
                rootIndex,
                sourceTypeCount,
                canonicalTypes.Count,
                nodeCount,
                addressCount,
                blobLength);
        }
        catch
        {
            HeapTemporaryArtifactCleanup.TryDeleteDirectory(spoolDirectory);
            throw;
        }
    }

    /// <summary>
    /// 判断序列化根类型是否为当前支持的 GC 堆转储包装类型。
    /// </summary>
    private static bool IsGcHeapDumpType(string rootType) =>
        string.Equals(rootType, "GCHeapDump", StringComparison.Ordinal)
        || rootType.EndsWith(".GCHeapDump", StringComparison.Ordinal)
        || rootType.EndsWith("+GCHeapDump", StringComparison.Ordinal);

    /// <summary>
    /// 校验 FastSerialization 类型表数量，避免恶意文件导致后续路径或循环溢出。
    /// </summary>
    private static void ValidateFastTypeCount(int typeCount)
    {
        if (typeCount < 0 || typeCount > 10_000_000)
        {
            throw new InvalidDataException("Invalid gcdump type count.");
        }
    }

    /// <summary>
    /// 校验 FastSerialization 节点数量，保证固定宽度临时工件长度可表示。
    /// </summary>
    private static void ValidateFastNodeCount(int nodeCount)
    {
        if (nodeCount < 0 || nodeCount > 100_000_000)
        {
            throw new InvalidDataException("Invalid gcdump node count.");
        }
    }

    /// <summary>
    /// 直接从已探测的 FastSerialization 小图生成紧凑索引；EventPipe 输入必须经固定宽度 spool 自动路由。
    /// </summary>
    private static SnapshotIndex ReadIndex(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryReadFastSerializationIndex(filePath, cancellationToken, out var serializedIndex))
        {
            return serializedIndex;
        }

        throw new DiagnosticsException(
            DiagnosticsErrorCode.CaptureFailed,
            "The gcdump FastSerialization format probe changed during parsing.");
    }

    /// <summary>
    /// 直接将 FastSerialization 节点、边和根投影为 <see cref="SnapshotIndex"/>。
    /// </summary>
    private static bool TryReadFastSerializationIndex(
        string filePath,
        CancellationToken cancellationToken,
        out SnapshotIndex index)
    {
        index = null!;
        try
        {
            using var reader = new FastSerializationReader(filePath);
            if (reader.ReadInt32() != 20
                || !string.Equals(reader.ReadUtf8(20), "!FastSerialization.1", StringComparison.Ordinal))
            {
                return false;
            }

            var rootType = reader.ReadObjectHeader();
            if (!string.Equals(rootType, "GCHeapDump", StringComparison.Ordinal)
                && !rootType.EndsWith(".GCHeapDump", StringComparison.Ordinal)
                && !rootType.EndsWith("+GCHeapDump", StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(reader.ReadObjectHeader(), "Graphs.MemoryGraph", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Unexpected gcdump graph type.");
            }

            _ = reader.ReadInt64();
            var rootIndex = reader.ReadInt32();
            var typeCount = reader.ReadInt32();
            if (typeCount < 0 || typeCount > 10_000_000)
            {
                throw new InvalidDataException("Invalid gcdump type count.");
            }

            var types = new (TypeIdentity Type, int Size)[typeCount];
            for (var typeIndex = 0; typeIndex < typeCount; typeIndex++)
            {
                var name = reader.ReadString() ?? $"Type(0x{typeIndex:x})";
                var size = reader.ReadInt32();
                types[typeIndex] = (new TypeIdentity(name, reader.ReadString()), size);
            }

            var nodeCount = reader.ReadInt32();
            if (nodeCount < 0 || nodeCount > 100_000_000)
            {
                throw new InvalidDataException("Invalid gcdump node count.");
            }

            var nodeLabels = new int[nodeCount];
            for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
            {
                nodeLabels[nodeIndex] = reader.ReadInt32();
            }

            var blobLength = reader.ReadInt32();
            if (blobLength < 0 || blobLength > reader.Remaining)
            {
                throw new InvalidDataException("Invalid gcdump node blob length.");
            }

            var blob = reader.ReadBytes(blobLength);
            var addressCount = reader.ReadInt32();
            if (addressCount < 0 || addressCount > nodeCount)
            {
                throw new InvalidDataException("Invalid gcdump address count.");
            }

            var addresses = new ulong[addressCount];
            for (var addressIndex = 0; addressIndex < addressCount; addressIndex++)
            {
                addresses[addressIndex] = unchecked((ulong)reader.ReadInt64());
            }

            if (!reader.TryReadTaggedByte(out _))
            {
                reader.ExpectTag(FastTag.EndObject);
            }

            var builder = new SnapshotIndexBuilder();
            var childNodeIndexes = new Dictionary<int, int[]>();
            for (var nodeIndex = 0; nodeIndex < addressCount; nodeIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = nodeLabels[nodeIndex];
                if (offset < 0 || offset >= blob.Length)
                {
                    continue;
                }

                var nodeReader = new SpanReader(blob.AsSpan(offset));
                var typeAndSize = nodeReader.ReadCompressedInt();
                var typeIndex = typeAndSize >> 1;
                if ((uint)typeIndex >= (uint)types.Length)
                {
                    continue;
                }

                var size = (typeAndSize & 1) != 0
                    ? nodeReader.ReadCompressedInt()
                    : types[typeIndex].Size;
                var childCount = nodeReader.ReadCompressedInt();
                var children = new int[Math.Max(0, childCount)];
                for (var childIndex = 0; childIndex < children.Length; childIndex++)
                {
                    children[childIndex] = checked(nodeIndex + nodeReader.ReadCompressedInt());
                }

                childNodeIndexes[nodeIndex] = children;
                if (size >= 0 && addresses[nodeIndex] != 0)
                {
                    builder.AddObject(addresses[nodeIndex], types[typeIndex].Type, size);
                }
            }

            foreach (var (nodeIndex, children) in childNodeIndexes)
            {
                if ((uint)nodeIndex >= (uint)addresses.Length || addresses[nodeIndex] == 0)
                {
                    continue;
                }

                builder.AddEdges(
                    addresses[nodeIndex],
                    children
                        .Where(child => (uint)child < (uint)addresses.Length)
                        .Select(child => addresses[child])
                        .ToArray());
            }

            if ((uint)rootIndex < (uint)addresses.Length)
            {
                if (childNodeIndexes.TryGetValue(rootIndex, out var rootChildren))
                {
                    foreach (var child in rootChildren.Where(child => (uint)child < (uint)addresses.Length))
                    {
                        builder.AddRoot(addresses[child]);
                    }
                }
                else
                {
                    builder.AddRoot(addresses[rootIndex]);
                }
            }

            index = builder.Build();
            return true;
        }
        catch (InvalidDataException exception)
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.CaptureFailed, "The gcdump could not be parsed.", exception);
        }
        catch (EndOfStreamException exception)
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.CaptureFailed, "The gcdump is truncated.", exception);
        }
    }

    private static async Task<IReadOnlyList<MemoryTypeSummary>> ReadTypeSummariesCoreAsync(
        string filePath,
        CancellationToken cancellationToken)
        => await ReadWithTemporaryIndexAsync(
            filePath,
            static handle => (IReadOnlyList<MemoryTypeSummary>)handle.TypeSummaries.ToArray(),
            cancellationToken).ConfigureAwait(false);

    private static async Task<IReadOnlyList<MemoryObjectInfo>> ReadObjectsCoreAsync(
        string filePath,
        TypeIdentity type,
        CancellationToken cancellationToken)
        => await ReadWithTemporaryIndexAsync(
            filePath,
            handle => (IReadOnlyList<MemoryObjectInfo>)handle.GetObjects(type).ToArray(),
            cancellationToken).ConfigureAwait(false);

    private static async Task<MemoryReferencePath?> ReadReferencePathCoreAsync(
        string filePath,
        ulong objectAddress,
        CancellationToken cancellationToken)
        => await ReadWithTemporaryIndexAsync(
            filePath,
            handle => handle.GetReferencePath(objectAddress, cancellationToken),
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 为不携带快照标识的兼容读取接口创建一次临时自动路由索引；大图仍使用磁盘工件，结果投影后立即释放并清理。
    /// </summary>
    /// <typeparam name="TResult">脱离索引句柄后仍可使用的查询结果类型。</typeparam>
    /// <param name="filePath">原始 .gcdump 路径，始终只读。</param>
    /// <param name="query">在临时句柄有效期内执行的查询投影。</param>
    /// <param name="cancellationToken">取消解析、构建或查询。</param>
    /// <returns>已从临时索引复制或构造的查询结果。</returns>
    private static async Task<TResult> ReadWithTemporaryIndexAsync<TResult>(
        string filePath,
        Func<HeapIndexHandle, TResult> query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var temporaryRoot = Path.GetTempPath();
        HeapTemporaryArtifactCleanup.TryDeleteAbandonedDirectories(
            temporaryRoot,
            "DotnetAnalysis.GCDumpQuery.*",
            HeapTemporaryArtifactCleanup.AbandonedDirectoryMinimumAge);
        var root = Path.Combine(temporaryRoot, $"DotnetAnalysis.GCDumpQuery.{Guid.NewGuid():N}");
        try
        {
            var router = new HeapIndexRouter(new SnapshotStorageLayout(root));
            using var handle = await BuildIndexCoreAsync(
                MemorySnapshotId.New(),
                filePath,
                router,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return query(handle);
        }
        finally
        {
            HeapTemporaryArtifactCleanup.TryDeleteDirectory(root);
        }
    }

    /// <summary>
    /// 尝试读取本项目写出的 FastSerialization .gcdump 格式。
    /// </summary>
    private static bool TryReadFastSerializationGcdump(
        string filePath,
        CancellationToken cancellationToken,
        out HeapData heap)
    {
        heap = null!;
        try
        {
            using var reader = new FastSerializationReader(filePath);
            if (reader.ReadInt32() != 20
                || !string.Equals(reader.ReadUtf8(20), "!FastSerialization.1", StringComparison.Ordinal))
            {
                return false;
            }

            var rootType = reader.ReadObjectHeader();
            if (!string.Equals(rootType, "GCHeapDump", StringComparison.Ordinal)
                && !rootType.EndsWith(".GCHeapDump", StringComparison.Ordinal)
                && !rootType.EndsWith("+GCHeapDump", StringComparison.Ordinal))
            {
                return false;
            }

            var graphType = reader.ReadObjectHeader();
            if (!string.Equals(graphType, "Graphs.MemoryGraph", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unexpected gcdump graph type '{graphType}'.");
            }

            var totalSize = reader.ReadInt64();
            _ = totalSize;
            var rootIndex = reader.ReadInt32();
            var typeCount = reader.ReadInt32();
            if (typeCount < 0 || typeCount > 10_000_000)
            {
                throw new InvalidDataException("Invalid gcdump type count.");
            }

            var types = new (string Name, int Size, string? Module)[typeCount];
            for (var index = 0; index < typeCount; index++)
            {
                types[index] = (
                    reader.ReadString() ?? $"Type(0x{index:x})",
                    reader.ReadInt32(),
                    reader.ReadString());
            }

            var nodeCount = reader.ReadInt32();
            if (nodeCount < 0 || nodeCount > 100_000_000)
            {
                throw new InvalidDataException("Invalid gcdump node count.");
            }

            var nodeLabels = new int[nodeCount];
            for (var index = 0; index < nodeCount; index++)
            {
                nodeLabels[index] = reader.ReadInt32();
            }

            var blobLength = reader.ReadInt32();
            if (blobLength < 0 || blobLength > reader.Remaining)
            {
                throw new InvalidDataException("Invalid gcdump node blob length.");
            }

            var blob = reader.ReadBytes(blobLength);
            var addressCount = reader.ReadInt32();
            if (addressCount < 0 || addressCount > nodeCount)
            {
                throw new InvalidDataException("Invalid gcdump address count.");
            }

            var addresses = new ulong[addressCount];
            for (var index = 0; index < addressCount; index++)
            {
                addresses[index] = unchecked((ulong)reader.ReadInt64());
            }

            // MemoryGraph writes an optional tagged Is64Bit value.  It is not
            // needed for the Core projection, but consume it when present so
            // malformed/truncated files are detected consistently.
            if (!reader.TryReadTaggedByte(out _))
            {
                reader.ExpectTag(FastTag.EndObject);
            }

            var aggregate = new Dictionary<TypeIdentity, (long Count, long Size)>();
            var objects = new List<MemoryObjectInfo>(Math.Min(addressCount, 1_000_000));
            var nodeChildren = new Dictionary<int, List<int>>();
            for (var nodeIndex = 0; nodeIndex < addressCount; nodeIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = nodeLabels[nodeIndex];
                if (offset < 0 || offset >= blob.Length)
                {
                    continue;
                }

                var nodeReader = new SpanReader(blob.AsSpan(offset));
                var typeAndSize = nodeReader.ReadCompressedInt();
                var typeIndex = typeAndSize >> 1;
                if ((uint)typeIndex >= (uint)types.Length)
                {
                    continue;
                }

                var size = (typeAndSize & 1) != 0
                    ? nodeReader.ReadCompressedInt()
                    : types[typeIndex].Size;
                var objectCount = nodeReader.ReadCompressedInt();
                var children = new List<int>(Math.Max(0, objectCount));
                for (var child = 0; child < objectCount; child++)
                {
                    children.Add(checked(nodeIndex + nodeReader.ReadCompressedInt()));
                }

                nodeChildren[nodeIndex] = children;

                if (size < 0 || nodeIndex >= addresses.Length || addresses[nodeIndex] == 0)
                {
                    continue;
                }

                var type = new TypeIdentity(types[typeIndex].Name, types[typeIndex].Module);
                var objectInfo = new MemoryObjectInfo(addresses[nodeIndex], type, size);
                objects.Add(objectInfo);
                aggregate.TryGetValue(type, out var current);
                aggregate[type] = (current.Count + 1, checked(current.Size + size));
            }

            var summaries = aggregate
                .Select(entry => new MemoryTypeSummary(entry.Key, entry.Value.Count, entry.Value.Size))
                .OrderByDescending(summary => summary.TotalSizeBytes)
                .ThenBy(summary => summary.Type.TypeName, StringComparer.Ordinal)
                .ToArray();
            var addressByNode = addresses;
            var edges = new Dictionary<ulong, IReadOnlyList<ulong>>();
            foreach (var pair in nodeChildren)
            {
                if (pair.Key < 0 || pair.Key >= addressByNode.Length || addressByNode[pair.Key] == 0)
                {
                    continue;
                }

                edges[addressByNode[pair.Key]] = pair.Value
                    .Where(child => child >= 0 && child < addressByNode.Length && addressByNode[child] != 0)
                    .Select(child => addressByNode[child])
                    .ToArray();
            }

            var serializedRoots = rootIndex >= 0 && rootIndex < addressByNode.Length
                ? nodeChildren.TryGetValue(rootIndex, out var rootChildren)
                    ? rootChildren
                        .Where(child => child >= 0 && child < addressByNode.Length && addressByNode[child] != 0)
                        .Select(child => addressByNode[child])
                        .Distinct()
                        .ToArray()
                    : addressByNode[rootIndex] != 0
                        ? new[] { addressByNode[rootIndex] }
                        : Array.Empty<ulong>()
                : Array.Empty<ulong>();
            heap = new HeapData(
                new ReadOnlyCollection<MemoryTypeSummary>(summaries),
                new ReadOnlyCollection<MemoryObjectInfo>(objects),
                edges,
                serializedRoots);
            return true;
        }
        catch (InvalidDataException exception)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.CaptureFailed,
                "The gcdump could not be parsed.",
                exception);
        }
        catch (EndOfStreamException exception)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.CaptureFailed,
                "The gcdump is truncated.",
                exception);
        }
    }

    /// <summary>
    /// 保存 FastSerialization 大型表的临时路径和计数，并将其投影为查询工件；对象、边和根只以固定宽度 spool 记录存在。
    /// </summary>
    private sealed class FastSerializationHeapSpool : IDisposable
    {
        private const int ObjectRecordBytes = sizeof(ulong) + sizeof(int) + sizeof(long);
        private readonly string _directory;
        private readonly string _spoolDirectory;
        private readonly int _rootNodeIndex;
        private readonly int _sourceTypeCount;
        private readonly int _canonicalTypeCount;
        private readonly int _nodeCount;
        private readonly int _addressCount;
        private readonly int _blobLength;
        private bool _disposed;

        /// <summary>
        /// 初始化一次 FastSerialization 流式投影的工作描述；所有路径均位于当前原子发布临时目录内。
        /// </summary>
        public FastSerializationHeapSpool(
            string directory,
            string spoolDirectory,
            int rootNodeIndex,
            int sourceTypeCount,
            int canonicalTypeCount,
            int nodeCount,
            int addressCount,
            int blobLength)
        {
            _directory = directory;
            _spoolDirectory = spoolDirectory;
            _rootNodeIndex = rootNodeIndex;
            _sourceTypeCount = sourceTypeCount;
            _canonicalTypeCount = canonicalTypeCount;
            _nodeCount = nodeCount;
            _addressCount = addressCount;
            _blobLength = blobLength;
        }

        /// <summary>
        /// 写入类型统计、对象、地址、正反向 CSR 和未知根证据工件；取消或异常时由发布器清理整个临时目录。
        /// </summary>
        public async Task<IReadOnlyList<string>> WriteArtifactsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var addressUnsortedPath = Path.Combine(_directory, "address-to-id.unsorted.bin");
                var typeObjectUnsortedPath = Path.Combine(_directory, "objects-by-type.unsorted.bin");
                var nodeToObjectPath = Path.Combine(_spoolDirectory, "node-to-object.bin");
                var objectCount = await WriteObjectArtifactsAsync(
                    addressUnsortedPath,
                    typeObjectUnsortedPath,
                    nodeToObjectPath,
                    cancellationToken).ConfigureAwait(false);

                var chunkBytes = HeapIndexResourcePolicy.GetExternalSortChunkBytes();
                var addressPath = Path.Combine(_directory, "address-to-id.bin");
                await HeapIndexExternalSorter.SortAddressIdRecordsAsync(addressUnsortedPath, addressPath, _directory, chunkBytes, cancellationToken).ConfigureAwait(false);
                await Task.Run(
                    () => ValidateAddressIndex(addressPath, objectCount, cancellationToken),
                    CancellationToken.None).ConfigureAwait(false);
                File.Delete(addressUnsortedPath);

                var sortedTypeObjectsPath = Path.Combine(_directory, "objects-by-type.sorted.bin");
                await HeapIndexExternalSorter.SortObjectIdEdgeRecordsAsync(typeObjectUnsortedPath, sortedTypeObjectsPath, _directory, chunkBytes, sortByTarget: false, cancellationToken).ConfigureAwait(false);
                File.Delete(typeObjectUnsortedPath);
                await WriteObjectsByTypeAsync(Path.Combine(_directory, "objects-by-type.bin"), sortedTypeObjectsPath, cancellationToken).ConfigureAwait(false);
                await WriteTypeSummaryAsync(Path.Combine(_directory, "type-summary.bin"), sortedTypeObjectsPath, cancellationToken).ConfigureAwait(false);
                File.Delete(sortedTypeObjectsPath);

                var edgeUnsortedPath = Path.Combine(_directory, "edges.unsorted.bin");
                var rootUnsortedPath = Path.Combine(_directory, "roots.unsorted.bin");
                await WriteEdgeAndRootSpoolsAsync(nodeToObjectPath, edgeUnsortedPath, rootUnsortedPath, cancellationToken).ConfigureAwait(false);

                var forwardEdgesPath = Path.Combine(_directory, "edges.forward.bin");
                var reverseEdgesPath = Path.Combine(_directory, "edges.reverse.bin");
                await HeapIndexExternalSorter.SortObjectIdEdgeRecordsAsync(edgeUnsortedPath, forwardEdgesPath, _directory, chunkBytes, sortByTarget: false, cancellationToken).ConfigureAwait(false);
                await HeapIndexExternalSorter.SortObjectIdEdgeRecordsAsync(edgeUnsortedPath, reverseEdgesPath, _directory, chunkBytes, sortByTarget: true, cancellationToken).ConfigureAwait(false);
                await Task.Run(
                    () => HeapIndexArtifactStore.WriteCsrFromSortedEdges(_directory, "forward", forwardEdgesPath, objectCount, sortByTarget: false, cancellationToken),
                    CancellationToken.None).ConfigureAwait(false);
                await Task.Run(
                    () => HeapIndexArtifactStore.WriteCsrFromSortedEdges(_directory, "reverse", reverseEdgesPath, objectCount, sortByTarget: true, cancellationToken),
                    CancellationToken.None).ConfigureAwait(false);
                File.Delete(edgeUnsortedPath);
                File.Delete(forwardEdgesPath);
                File.Delete(reverseEdgesPath);

                var sortedRootsPath = Path.Combine(_directory, "roots.sorted.bin");
                await HeapIndexExternalSorter.SortObjectIdEdgeRecordsAsync(rootUnsortedPath, sortedRootsPath, _directory, chunkBytes, sortByTarget: false, cancellationToken).ConfigureAwait(false);
                File.Delete(rootUnsortedPath);
                await WriteUnknownRootEvidenceArtifactsAsync(objectCount, sortedRootsPath, cancellationToken).ConfigureAwait(false);
                File.Delete(sortedRootsPath);

                return
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
            }
            catch (DiagnosticsException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException or OverflowException or ArgumentException or JsonException)
            {
                throw new DiagnosticsException(DiagnosticsErrorCode.SnapshotIndexBuildFailed, "无法从 GCDump 构建磁盘堆索引。", exception);
            }
        }

        /// <summary>
        /// 顺序解码每个可寻址节点，写出致密对象行、按地址外排记录、按类型分页记录以及 nodeId 到 objectId 映射。
        /// </summary>
        private async Task<int> WriteObjectArtifactsAsync(
            string addressUnsortedPath,
            string typeObjectUnsortedPath,
            string nodeToObjectPath,
            CancellationToken cancellationToken)
        {
            await using var objectStream = new FileStream(Path.Combine(_directory, "objects.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var addressStream = new FileStream(addressUnsortedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var typeObjectStream = new FileStream(typeObjectUnsortedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var nodeObjectStream = new FileStream(nodeToObjectPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var objects = new BinaryWriter(objectStream, Encoding.UTF8, leaveOpen: true);
            using var addresses = new BinaryWriter(addressStream, Encoding.UTF8, leaveOpen: true);
            using var typeObjects = new BinaryWriter(typeObjectStream, Encoding.UTF8, leaveOpen: true);
            using var nodeObjects = new BinaryWriter(nodeObjectStream, Encoding.UTF8, leaveOpen: true);
            using var labels = new BinaryReader(File.OpenRead(Path.Combine(_spoolDirectory, "node-labels.bin")), Encoding.UTF8, leaveOpen: false);
            using var addressesInput = new BinaryReader(File.OpenRead(Path.Combine(_spoolDirectory, "addresses.bin")), Encoding.UTF8, leaveOpen: false);
            using var blob = OpenBlobStream();
            using var typeSizes = new FixedInt32Lookup(Path.Combine(_spoolDirectory, "type-sizes.bin"), _sourceTypeCount);
            using var typeIndexes = new FixedInt32Lookup(Path.Combine(_spoolDirectory, "type-indexes.bin"), _sourceTypeCount);
            var objectCount = 0;
            for (var nodeIndex = 0; nodeIndex < _nodeCount; nodeIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var label = labels.ReadInt32();
                var address = nodeIndex < _addressCount ? addressesInput.ReadUInt64() : 0UL;
                var objectId = -1;
                if (nodeIndex < _addressCount && TryReadNode(blob, label, out var node) && (uint)node.TypeIndex < (uint)_sourceTypeCount)
                {
                    var size = node.HasExplicitSize ? node.ExplicitSize : typeSizes.Read(node.TypeIndex);
                    if (size >= 0 && address != 0)
                    {
                        var canonicalTypeIndex = typeIndexes.Read(node.TypeIndex);
                        if ((uint)canonicalTypeIndex >= (uint)_canonicalTypeCount)
                        {
                            throw new InvalidDataException("FastSerialization 规范类型索引无效。");
                        }

                        objectId = objectCount++;
                        objects.Write(address);
                        objects.Write(canonicalTypeIndex);
                        objects.Write((long)size);
                        addresses.Write(address);
                        addresses.Write(objectId);
                        typeObjects.Write(canonicalTypeIndex);
                        typeObjects.Write(objectId);
                    }
                }

                nodeObjects.Write(objectId);
            }

            if (labels.BaseStream.Position != labels.BaseStream.Length || addressesInput.BaseStream.Position != addressesInput.BaseStream.Length)
            {
                throw new InvalidDataException("FastSerialization 固定宽度 spool 长度无效。");
            }

            await objectStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await addressStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await typeObjectStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await nodeObjectStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return objectCount;
        }

        /// <summary>
        /// 验证按地址排序的对象索引严格递增且不含零地址或越界 objectId，避免发布后映射查询产生歧义。
        /// </summary>
        /// <param name="path">已排序的地址到 objectId 固定宽度索引。</param>
        /// <param name="objectCount">对象工件中的对象数量。</param>
        /// <param name="cancellationToken">取消完整性扫描。</param>
        private static void ValidateAddressIndex(string path, int objectCount, CancellationToken cancellationToken)
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            var recordCount = 0;
            var hasPrevious = false;
            ulong previousAddress = 0;
            while (stream.Position < stream.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stream.Length - stream.Position < sizeof(ulong) + sizeof(int))
                {
                    throw new InvalidDataException("FastSerialization 地址索引记录截断。");
                }

                var address = reader.ReadUInt64();
                var objectId = reader.ReadInt32();
                if (address == 0)
                {
                    throw new InvalidDataException("FastSerialization 地址索引不得包含零地址。");
                }

                if ((uint)objectId >= (uint)objectCount)
                {
                    throw new InvalidDataException("FastSerialization 地址索引包含越界 objectId。");
                }

                if (hasPrevious && address <= previousAddress)
                {
                    throw new InvalidDataException("FastSerialization 对象地址必须唯一且严格递增。");
                }

                hasPrevious = true;
                previousAddress = address;
                recordCount = checked(recordCount + 1);
            }

            if (recordCount != objectCount)
            {
                throw new InvalidDataException("FastSerialization 地址索引记录数与对象数量不一致。");
            }
        }

        /// <summary>
        /// 顺序投影按类型排序记录为 objectId 列表，保持 <c>MappedHeapIndex</c> 的类型分页布局。
        /// </summary>
        private static async Task WriteObjectsByTypeAsync(string outputPath, string sortedInputPath, CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            using var reader = new BinaryReader(File.OpenRead(sortedInputPath), Encoding.UTF8, leaveOpen: false);
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = reader.ReadInt32();
                writer.Write(reader.ReadInt32());
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 对类型数使用固定大小统计数组、对对象使用顺序扫描，随后顺序读取类型表并按既有公开排序写出统计，避免对象 DTO 或类型字典。
        /// </summary>
        private async Task WriteTypeSummaryAsync(string outputPath, string sortedTypeObjectsPath, CancellationToken cancellationToken)
        {
            var counts = new long[_canonicalTypeCount];
            var sizes = new long[_canonicalTypeCount];
            using (var objects = new BinaryReader(File.OpenRead(Path.Combine(_directory, "objects.bin")), Encoding.UTF8, leaveOpen: false))
            {
                while (objects.BaseStream.Position < objects.BaseStream.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _ = objects.ReadUInt64();
                    var typeIndex = objects.ReadInt32();
                    var size = objects.ReadInt64();
                    if ((uint)typeIndex >= (uint)_canonicalTypeCount || size < 0)
                    {
                        throw new InvalidDataException("FastSerialization 对象工件类型或大小无效。");
                    }

                    counts[typeIndex] = checked(counts[typeIndex] + 1);
                    sizes[typeIndex] = checked(sizes[typeIndex] + size);
                }
            }

            await using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var json = new Utf8JsonWriter(stream);
            using var types = new BinaryReader(File.OpenRead(Path.Combine(_directory, "types.bin")), Encoding.UTF8, leaveOpen: false);
            if (types.ReadInt32() != _canonicalTypeCount)
            {
                throw new InvalidDataException("FastSerialization 类型工件计数无效。");
            }

            var summaries = new List<MemoryTypeSummary>();
            for (var typeIndex = 0; typeIndex < _canonicalTypeCount; typeIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = types.ReadString();
                var assembly = types.ReadString();
                summaries.Add(new MemoryTypeSummary(
                    new TypeIdentity(name, string.IsNullOrEmpty(assembly) ? null : assembly),
                    counts[typeIndex],
                    sizes[typeIndex]));
            }

            if (types.BaseStream.Position != types.BaseStream.Length)
            {
                throw new InvalidDataException("FastSerialization 类型工件包含尾部数据。");
            }

            json.WriteStartArray();
            foreach (var summary in summaries
                .OrderByDescending(item => item.TotalSizeBytes)
                .ThenBy(item => item.Type.TypeName, StringComparer.Ordinal))
            {
                JsonSerializer.Serialize(json, summary);
            }

            json.WriteEndArray();
            await json.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 解码节点相对索引边并转换为 objectId 边；根节点子项写为未知根，绝不伪造函数或模块证据。
        /// </summary>
        private async Task WriteEdgeAndRootSpoolsAsync(
            string nodeToObjectPath,
            string edgePath,
            string rootPath,
            CancellationToken cancellationToken)
        {
            await using var edgeStream = new FileStream(edgePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var rootStream = new FileStream(rootPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var edges = new BinaryWriter(edgeStream, Encoding.UTF8, leaveOpen: true);
            using var roots = new BinaryWriter(rootStream, Encoding.UTF8, leaveOpen: true);
            using var labels = new BinaryReader(File.OpenRead(Path.Combine(_spoolDirectory, "node-labels.bin")), Encoding.UTF8, leaveOpen: false);
            using var sourceIds = new BinaryReader(File.OpenRead(nodeToObjectPath), Encoding.UTF8, leaveOpen: false);
            using var nodeIds = new FixedInt32Lookup(nodeToObjectPath, _nodeCount);
            using var blob = OpenBlobStream();
            for (var nodeIndex = 0; nodeIndex < _nodeCount; nodeIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var label = labels.ReadInt32();
                var sourceId = sourceIds.ReadInt32();
                if (!TryReadNode(blob, label, out var node))
                {
                    if (nodeIndex == _rootNodeIndex && sourceId >= 0)
                    {
                        roots.Write(sourceId);
                        roots.Write(0);
                    }

                    continue;
                }

                for (var childIndex = 0; childIndex < node.ChildCount; childIndex++)
                {
                    var relativeNodeIndex = checked(nodeIndex + node.ReadChildRelativeIndex());
                    if ((uint)relativeNodeIndex >= (uint)_nodeCount)
                    {
                        continue;
                    }

                    var targetId = nodeIds.Read(relativeNodeIndex);
                    if (sourceId >= 0 && targetId >= 0)
                    {
                        edges.Write(sourceId);
                        edges.Write(targetId);
                    }

                    if (nodeIndex == _rootNodeIndex && targetId >= 0)
                    {
                        roots.Write(targetId);
                        roots.Write(0);
                    }
                }
            }

            if (labels.BaseStream.Position != labels.BaseStream.Length || sourceIds.BaseStream.Position != sourceIds.BaseStream.Length)
            {
                throw new InvalidDataException("FastSerialization 节点映射 spool 长度无效。");
            }

            await edgeStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await rootStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 从根对象排序记录写入按对象偏移的未知根证据；重复根对象仅保留一条与内存索引相同的证据。
        /// </summary>
        private async Task WriteUnknownRootEvidenceArtifactsAsync(int objectCount, string sortedRootsPath, CancellationToken cancellationToken)
        {
            await using var offsetStream = new FileStream(Path.Combine(_directory, "roots-by-object.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var evidenceStream = new FileStream(Path.Combine(_directory, "root-evidence.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var offsets = new BinaryWriter(offsetStream, Encoding.UTF8, leaveOpen: true);
            using var evidence = new BinaryWriter(evidenceStream, Encoding.UTF8, leaveOpen: true);
            using var roots = new BinaryReader(File.OpenRead(sortedRootsPath), Encoding.UTF8, leaveOpen: false);
            var hasRoot = TryReadObjectIdPair(roots, out var pendingObjectId, out _);
            for (var objectId = 0; objectId < objectCount; objectId++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                offsets.Write(evidenceStream.Position);
                if (hasRoot && pendingObjectId == objectId)
                {
                    WriteUnknownRootEvidence(evidence);
                    do
                    {
                        hasRoot = TryReadObjectIdPair(roots, out pendingObjectId, out _);
                    }
                    while (hasRoot && pendingObjectId == objectId);
                }
            }

            if (hasRoot)
            {
                throw new InvalidDataException("FastSerialization 根对象标识超出对象范围。");
            }

            offsets.Write(evidenceStream.Position);
            await offsetStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await evidenceStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 打开一个支持位置读取的 blob 流；只在当前节点解码期间持有其窗口缓冲区。
        /// </summary>
        private FileStream OpenBlobStream() => new(
            Path.Combine(_spoolDirectory, "node-blob.bin"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.RandomAccess);

        /// <summary>
        /// 从节点标签定位并开始读取一条变长节点记录；无效标签保留旧读取器的跳过行为。
        /// </summary>
        private bool TryReadNode(FileStream blob, int label, out FastSerializationNodeReader node)
        {
            if (label < 0 || label >= _blobLength)
            {
                node = default;
                return false;
            }

            blob.Position = label;
            node = new FastSerializationNodeReader(blob, _blobLength);
            _ = node.TypeAndSize;
            _ = node.ChildCount;
            return true;
        }

        /// <summary>
        /// 释放本次构建的 labels、blob、地址、类型大小和节点映射 spool；已完成基础工件不受影响。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            HeapTemporaryArtifactCleanup.TryDeleteDirectory(_spoolDirectory);
        }
    }

    /// <summary>
    /// 表示 FastSerialization 预读得到的路由计数；边数为无需大数组即可获得的保守下界。
    /// </summary>
    private readonly record struct FastSerializationGraphCounts(int AddressCount, int NodeCount, int EstimatedEdgeCount);

    /// <summary>
    /// 在固定宽度 Int32 文件中随机读取一个值，避免创建 nodeId 到 objectId 的托管数组。
    /// </summary>
    private sealed class FixedInt32Lookup : IDisposable
    {
        private readonly FileStream _stream;
        private readonly BinaryReader _reader;
        private readonly int _count;

        /// <summary>
        /// 打开并验证固定宽度 Int32 spool。
        /// </summary>
        public FixedInt32Lookup(string path, int count)
        {
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
            _count = count;
            if (_stream.Length != checked((long)count * sizeof(int)))
            {
                _stream.Dispose();
                throw new InvalidDataException("FastSerialization Int32 spool 长度无效。");
            }

            _reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
        }

        /// <summary>
        /// 读取指定零基序号的值。
        /// </summary>
        public int Read(int index)
        {
            if ((uint)index >= (uint)_count)
            {
                throw new InvalidDataException("FastSerialization Int32 spool 索引越界。");
            }

            _stream.Position = (long)index * sizeof(int);
            return _reader.ReadInt32();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _reader.Dispose();
            _stream.Dispose();
        }
    }

    /// <summary>
    /// 以固定 blob 边界解码一条节点记录，并按需读取相对 child 索引，避免构造 child 数组。
    /// </summary>
    private struct FastSerializationNodeReader
    {
        private readonly FileStream _stream;
        private readonly long _end;
        private readonly int _typeAndSize;
        private readonly int _explicitSize;
        private readonly int _childCount;

        /// <summary>
        /// 从当前 blob 位置读取节点头。
        /// </summary>
        public FastSerializationNodeReader(FileStream stream, int blobLength)
        {
            _stream = stream;
            _end = blobLength;
            _typeAndSize = ReadCompressedInt();
            _explicitSize = (_typeAndSize & 1) != 0 ? ReadCompressedInt() : 0;
            _childCount = Math.Max(0, ReadCompressedInt());
        }

        /// <summary>
        /// 获取编码的类型和大小标志。
        /// </summary>
        public int TypeAndSize => _typeAndSize;

        /// <summary>
        /// 获取类型表索引。
        /// </summary>
        public int TypeIndex => _typeAndSize >> 1;

        /// <summary>
        /// 获取节点是否显式编码大小。
        /// </summary>
        public bool HasExplicitSize => (_typeAndSize & 1) != 0;

        /// <summary>
        /// 获取显式编码的对象大小。
        /// </summary>
        public int ExplicitSize => _explicitSize;

        /// <summary>
        /// 获取已归零处理的 child 数量。
        /// </summary>
        public int ChildCount => _childCount;

        /// <summary>
        /// 读取下一条相对于当前节点的 child 索引增量。
        /// </summary>
        public int ReadChildRelativeIndex() => ReadCompressedInt();

        /// <summary>
        /// 按 FastSerialization 压缩有符号整数规则读取一个值并严格限制在当前 blob 内。
        /// </summary>
        private int ReadCompressedInt()
        {
            var result = 0;
            var byteCount = 0;
            byte value;
            do
            {
                if (byteCount == 5)
                {
                    throw new InvalidDataException("FastSerialization 压缩整数超过五个字节。");
                }

                value = ReadByte();
                result = unchecked((result << 7) | (value & 0x7F));
                byteCount++;
            }
            while ((value & 0x80) != 0);

            if (byteCount < 5)
            {
                var bitCount = byteCount * 7;
                if ((result & (1 << (bitCount - 1))) != 0)
                {
                    result |= -1 << bitCount;
                }
            }

            return result;
        }

        /// <summary>
        /// 读取一个 blob 字节并在截断时失败。
        /// </summary>
        private byte ReadByte()
        {
            if (_stream.Position >= _end)
            {
                throw new EndOfStreamException();
            }

            var value = _stream.ReadByte();
            return value < 0 ? throw new EndOfStreamException() : (byte)value;
        }

    }

    /// <summary>
    /// 尝试读取一条固定宽度 objectId/辅助值记录，文件截断必须在完整记录边界报告失败。
    /// </summary>
    private static bool TryReadObjectIdPair(BinaryReader reader, out int objectId, out int value)
    {
        if (reader.BaseStream.Position == reader.BaseStream.Length)
        {
            objectId = default;
            value = default;
            return false;
        }

        if (reader.BaseStream.Length - reader.BaseStream.Position < sizeof(int) * 2)
        {
            throw new InvalidDataException("FastSerialization objectId spool 截断。");
        }

        objectId = reader.ReadInt32();
        value = reader.ReadInt32();
        return true;
    }

    /// <summary>
    /// 以与映射根证据读取器一致的二进制布局写入一条未知根，不声称存在函数或模块证据。
    /// </summary>
    private static void WriteUnknownRootEvidence(BinaryWriter writer)
    {
        writer.Write((byte)MemoryRootKind.Unknown);
        writer.Write((int)MemoryRootFlags.None);
        writer.Write(-1);
        writer.Write(-1);
    }

    /// <summary>
    /// FastSerialization 流中使用的标记字节。
    /// </summary>
    private enum FastTag : byte
    {
        BeginObject = 4,
        NullReference = 1,
        EndObject = 6,
        Byte = 8
    }

    /// <summary>
    /// 对 FastSerialization 文件提供带截断检查的基础读取操作。
    /// </summary>
    private sealed class FastSerializationReader : IDisposable
    {
        private readonly FileStream _stream;
        private readonly BinaryReader _binaryReader;

        /// <summary>
        /// 打开指定快照文件并初始化 UTF-8 二进制读取器。
        /// </summary>
        /// <param name="filePath">待读取的快照路径。</param>
        public FastSerializationReader(string filePath)
        {
            _stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _binaryReader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
        }

        /// <summary>
        /// 获取当前读取位置之后剩余的字节数。
        /// </summary>
        public long Remaining => _stream.Length - _stream.Position;

        /// <summary>
        /// 读取一个 32 位有符号整数。
        /// </summary>
        public int ReadInt32() => _binaryReader.ReadInt32();

        /// <summary>
        /// 读取一个 64 位有符号整数。
        /// </summary>
        public long ReadInt64() => _binaryReader.ReadInt64();

        /// <summary>
        /// 读取一个字节。
        /// </summary>
        public byte ReadByte() => _binaryReader.ReadByte();

        /// <summary>
        /// 读取指定数量的字节；不足时报告截断。
        /// </summary>
        /// <param name="count">期望读取的字节数。</param>
        public byte[] ReadBytes(int count)
        {
            var bytes = _binaryReader.ReadBytes(count);
            if (bytes.Length != count)
            {
                throw new EndOfStreamException();
            }

            return bytes;
        }

        /// <summary>
        /// 在不创建大数组的前提下将指定数量的原始字节复制到目标流；输入不足时报告截断。
        /// </summary>
        /// <param name="destination">接收字节的已打开输出流。</param>
        /// <param name="count">必须复制的字节数。</param>
        /// <param name="cancellationToken">复制期间的取消令牌。</param>
        public async Task CopyBytesToAsync(Stream destination, long count, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(destination);
            if (count < 0 || count > Remaining)
            {
                throw new EndOfStreamException();
            }

            var buffer = new byte[64 * 1024];
            var remaining = count;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = (int)Math.Min(buffer.Length, remaining);
                var read = await _stream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }

        /// <summary>
        /// 跳过指定的原始字节区间并验证其不越过文件末尾。
        /// </summary>
        /// <param name="count">待跳过的字节数。</param>
        public void SkipBytes(long count)
        {
            if (count < 0 || count > Remaining)
            {
                throw new EndOfStreamException();
            }

            _stream.Position += count;
        }

        /// <summary>
        /// 按 UTF-8 解码指定长度的字符串。
        /// </summary>
        /// <param name="byteCount">字符串占用的字节数。</param>
        public string ReadUtf8(int byteCount) => Encoding.UTF8.GetString(ReadBytes(byteCount));

        /// <summary>
        /// 读取带长度前缀的可空字符串。
        /// </summary>
        public string? ReadString()
        {
            var byteCount = ReadInt32();
            return byteCount < 0 ? null : ReadUtf8(byteCount);
        }

        /// <summary>
        /// 跳过带长度前缀的字符串，不为预读计数或 spool 阶段创建临时字符串对象。
        /// </summary>
        /// <param name="nullable">是否允许负一长度表示空值。</param>
        public void SkipString(bool nullable)
        {
            var byteCount = ReadInt32();
            if (byteCount == -1 && nullable)
            {
                return;
            }

            if (byteCount < 0)
            {
                throw new InvalidDataException("Invalid FastSerialization string length.");
            }

            SkipBytes(byteCount);
        }

        /// <summary>
        /// 读取对象头并返回其序列化类型全名。
        /// </summary>
        public string ReadObjectHeader()
        {
            var tag = (FastTag)ReadByte();
            if (tag is not (FastTag.BeginObject or (FastTag)5))
            {
                throw new InvalidDataException($"Expected an object tag, got {(byte)tag}.");
            }

            var typeTag = (FastTag)ReadByte();
            if (typeTag is not (FastTag.BeginObject or (FastTag)5))
            {
                throw new InvalidDataException("Missing SerializationType object.");
            }

            // SerializationType itself has no type descriptor, so Serializer
            // writes a NullReference marker before its raw version fields.
            ExpectTag(FastTag.NullReference);
            _ = ReadInt32(); // serialization version
            _ = ReadInt32(); // minimum reader version
            var fullName = ReadString() ?? throw new InvalidDataException("SerializationType name is missing.");
            ExpectTag(FastTag.EndObject);
            return fullName;
        }

        /// <summary>
        /// 尝试读取可选的带标签字节，不匹配时回退一个字节。
        /// </summary>
        public bool TryReadTaggedByte(out byte value)
        {
            var tag = (FastTag)ReadByte();
            if (tag == FastTag.Byte)
            {
                value = ReadByte();
                return true;
            }

            _stream.Position--;
            value = 0;
            return false;
        }

        /// <summary>
        /// 读取并校验下一个流标记。
        /// </summary>
        /// <param name="expected">期望出现的标记。</param>
        public void ExpectTag(FastTag expected)
        {
            var actual = (FastTag)ReadByte();
            if (actual != expected)
            {
                throw new InvalidDataException($"Expected tag {expected}, got {actual}.");
            }
        }

        /// <summary>
        /// 释放底层文件流和二进制读取器。
        /// </summary>
        public void Dispose()
        {
            _binaryReader.Dispose();
            _stream.Dispose();
        }
    }

    /// <summary>
    /// 在节点二进制缓冲区上读取压缩整数的轻量读取器。
    /// </summary>
    private ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> _buffer;
        private int _position;

        /// <summary>
        /// 从指定缓冲区的起始位置开始读取。
        /// </summary>
        public SpanReader(ReadOnlySpan<byte> buffer)
        {
            _buffer = buffer;
            _position = 0;
        }

        /// <summary>
        /// 按 FastSerialization 编码读取一个压缩有符号整数。
        /// </summary>
        public int ReadCompressedInt()
        {
            var result = 0;
            var byteCount = 0;
            byte value;
            do
            {
                if (byteCount == 5)
                {
                    throw new InvalidDataException("FastSerialization compressed integer exceeds five bytes.");
                }

                value = ReadByte();
                result = unchecked((result << 7) | (value & 0x7F));
                byteCount++;
            }
            while ((value & 0x80) != 0);

            if (byteCount < 5)
            {
                var bitCount = byteCount * 7;
                if ((result & (1 << (bitCount - 1))) != 0)
                {
                    result |= -1 << bitCount;
                }
            }

            return result;
        }

        /// <summary>
        /// 读取下一个缓冲区字节，越界时报告截断。
        /// </summary>
        private byte ReadByte()
        {
            if ((uint)_position >= (uint)_buffer.Length)
            {
                throw new EndOfStreamException();
            }

            return _buffer[_position++];
        }

    }

    /// <summary>
    /// 读取器内部统一使用的堆对象、类型、边和根集合。
    /// </summary>
    internal sealed record HeapData(
        IReadOnlyList<MemoryTypeSummary> TypeSummaries,
        IReadOnlyList<MemoryObjectInfo> Objects,
        IReadOnlyDictionary<ulong, IReadOnlyList<ulong>> Edges,
        IReadOnlyList<ulong> Roots);

    /// <summary>
    /// 暂存 EventPipe 节点及其后续边数量。
    /// </summary>
    private sealed record NodeData(MemoryObjectInfo Object, long EdgeCount);

    /// <summary>
    /// 按节点声明的边数量把扁平目标地址切分为邻接表。
    /// </summary>
    private static Dictionary<ulong, IReadOnlyList<ulong>> BuildEdges(
        IReadOnlyList<NodeData> nodes,
        List<ulong> edgeTargets)
    {
        var edges = new Dictionary<ulong, IReadOnlyList<ulong>>();
        var edgeIndex = 0;
        foreach (var node in nodes)
        {
            var count = node.EdgeCount > 0
                ? Math.Min(node.EdgeCount, edgeTargets.Count - edgeIndex)
                : 0;
            if (count > 0)
            {
                edges[node.Object.Address] = edgeTargets
                    .Skip(edgeIndex)
                    .Take((int)count)
                    .Where(address => address != 0)
                    .ToArray();
                edgeIndex += (int)count;
            }
            else
            {
                edges[node.Object.Address] = Array.Empty<ulong>();
            }
        }

        return edges;
    }

    /// <summary>
    /// 优先从根节点正向搜索，必要时通过反向边构造引用路径。
    /// </summary>
    private static MemoryReferencePath? BuildReferencePath(HeapData heap, ulong objectAddress)
    {
        var objectsByAddress = heap.Objects
            .GroupBy(candidate => candidate.Address)
            .ToDictionary(group => group.Key, group => group.First());
        if (!objectsByAddress.ContainsKey(objectAddress))
        {
            return null;
        }

        var queue = new Queue<ulong>();
        var parent = new Dictionary<ulong, ulong?>();
        var roots = heap.Roots
            .Where(objectsByAddress.ContainsKey)
            .Distinct()
            .ToArray();
        var referenced = heap.Edges.Values
            .SelectMany(children => children)
            .ToHashSet();
        roots = roots
            .Concat(objectsByAddress.Keys.Where(address => !referenced.Contains(address)))
            .Distinct()
            .ToArray();

        foreach (var root in roots)
        {
            parent[root] = null;
            queue.Enqueue(root);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == objectAddress)
            {
                var path = new List<MemoryObjectInfo>();
                ulong? cursor = current;
                while (cursor is not null)
                {
                    path.Add(objectsByAddress[cursor.Value]);
                    cursor = parent[cursor.Value];
                }

                path.Reverse();
                return new MemoryReferencePath(objectAddress, path);
            }

            if (!heap.Edges.TryGetValue(current, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (objectsByAddress.ContainsKey(child) && parent.TryAdd(child, current))
                {
                    queue.Enqueue(child);
                }
            }
        }

        var reverseEdges = new Dictionary<ulong, List<ulong>>();
        foreach (var edge in heap.Edges)
        {
            foreach (var child in edge.Value)
            {
                reverseEdges.TryGetValue(child, out var parents);
                parents ??= [];
                parents.Add(edge.Key);
                reverseEdges[child] = parents;
            }
        }

        var reverseQueue = new Queue<ulong>();
        var next = new Dictionary<ulong, ulong?> { [objectAddress] = null };
        reverseQueue.Enqueue(objectAddress);
        ulong? chainStart = null;
        while (reverseQueue.Count > 0)
        {
            var current = reverseQueue.Dequeue();
            if (roots.Contains(current)
                || !reverseEdges.TryGetValue(current, out var currentParents)
                || currentParents.Count == 0)
            {
                chainStart = current;
                break;
            }

            foreach (var parentCandidate in currentParents)
            {
                if (next.TryAdd(parentCandidate, current))
                {
                    reverseQueue.Enqueue(parentCandidate);
                }
            }
        }

        if (chainStart is not null)
        {
            var path = new List<MemoryObjectInfo>();
            ulong? cursor = chainStart;
            while (cursor is not null)
            {
                path.Add(objectsByAddress[cursor.Value]);
                cursor = next[cursor.Value];
            }

            path.Reverse();
            return new MemoryReferencePath(objectAddress, path);
        }

        return null;
    }
}
