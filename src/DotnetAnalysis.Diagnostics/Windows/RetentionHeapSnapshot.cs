using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 定义可构建或打开可查询堆索引句柄的内部快照读取器边界，避免分析服务依赖某个具体文件格式或索引介质。
/// </summary>
internal interface IIndexedMemorySnapshotReader : IMemorySnapshotReader
{
    /// <summary>
    /// 为指定快照构建或打开可查询索引句柄；大型快照由读取器和路由器协作直接发布磁盘工件。
    /// </summary>
    /// <param name="snapshotId">快照标识，用于定位其索引工件族。</param>
    /// <param name="filePath">待解析快照的本地绝对路径。</param>
    /// <param name="router">负责根据资源策略选择内存或磁盘索引的内部路由器。</param>
    /// <param name="cancellationToken">取消当前共享构建；调用方等待取消由缓存单独处理。</param>
    /// <returns>已验证且可查询的索引句柄。</returns>
    Task<HeapIndexHandle> BuildIndexAsync(
        MemorySnapshotId snapshotId,
        string filePath,
        HeapIndexRouter router,
        CancellationToken cancellationToken);
}

/// <summary>
/// 定义保留分析专用的、带完成标记和 SHA-256 校验的紧凑堆快照格式。
/// </summary>
/// <remarks>
/// 此格式只存在于 Diagnostics 层。写入端先留下未完成头部，只有全部负载和校验值写入成功后才原子标记完成；
/// 读取端在按声明长度分配前校验魔数、版本、文件长度、完成标记和整个负载校验和。
/// </remarks>
internal static class RetentionHeapSnapshot
{
    private static readonly byte[] s_magic = "DARSNP01"u8.ToArray();
    private const int FormatVersion = 1;
    private const int HeaderLength = 64;
    private const int HashOffset = 24;
    private const int Sha256HashLength = 32;
    private const int CompleteOffset = 12;
    private const int MaximumStringBytes = 1_048_576;
    private const long MaximumSnapshotBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumTypeCount = 1_000_000;
    private const int MaximumObjectCount = RetentionProfilerRawCaptureSpool.MaximumObjectCount;
    private const int MaximumEdgeCount = RetentionProfilerRawCaptureSpool.MaximumEdgeCount;
    private const int MaximumRootCount = RetentionProfilerRawCaptureSpool.MaximumRootCount;

    /// <summary>
    /// 将经过 Controller 校验的对象、引用和根证据写入临时保留快照文件。
    /// </summary>
    /// <param name="filePath">不存在的临时目标文件路径。</param>
    /// <param name="data">待持久化的堆图和根证据。</param>
    /// <param name="cancellationToken">取消当前文件写入的令牌。</param>
    /// <exception cref="DiagnosticsException">数据超过保留分析的单快照上限时引发。</exception>
    public static async Task WriteAsync(
        string filePath,
        SnapshotData data,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(data);
        ValidateDataCounts(data);
        cancellationToken.ThrowIfCancellationRequested();

        await using var stream = new FileStream(
            filePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.WriteAsync(new byte[HeaderLength], cancellationToken).ConfigureAwait(false);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var writer = new PayloadWriter(stream, hash, cancellationToken);
        await writer.WriteInt32Async(data.Types.Count).ConfigureAwait(false);
        foreach (var type in data.Types)
        {
            ArgumentNullException.ThrowIfNull(type);
            await writer.WriteStringAsync(type.TypeName).ConfigureAwait(false);
            await writer.WriteNullableStringAsync(type.AssemblyName).ConfigureAwait(false);
        }

        await writer.WriteInt32Async(data.Objects.Count).ConfigureAwait(false);
        foreach (var item in data.Objects)
        {
            ValidateObject(item, data.Types.Count);
            await writer.WriteUInt64Async(item.Address).ConfigureAwait(false);
            await writer.WriteInt32Async(item.TypeIndex).ConfigureAwait(false);
            await writer.WriteInt64Async(item.SizeBytes).ConfigureAwait(false);
        }

        await writer.WriteInt32Async(data.Edges.Count).ConfigureAwait(false);
        foreach (var edge in data.Edges)
        {
            await writer.WriteUInt64Async(edge.SourceAddress).ConfigureAwait(false);
            await writer.WriteUInt64Async(edge.TargetAddress).ConfigureAwait(false);
        }

        await writer.WriteInt32Async(data.Roots.Count).ConfigureAwait(false);
        foreach (var root in data.Roots)
        {
            ArgumentNullException.ThrowIfNull(root.Root);
            await writer.WriteUInt64Async(root.ObjectAddress).ConfigureAwait(false);
            await writer.WriteByteAsync(checked((byte)root.Root.Kind)).ConfigureAwait(false);
            await writer.WriteInt32Async(checked((int)root.Root.Flags)).ConfigureAwait(false);
            await writer.WriteNullableStringAsync(root.Root.FunctionName).ConfigureAwait(false);
            await writer.WriteNullableStringAsync(root.Root.ModuleName).ConfigureAwait(false);
        }

        await writer.FlushAsync().ConfigureAwait(false);
        var checksum = hash.GetHashAndReset();
        stream.Position = 0;
        var header = new byte[HeaderLength];
        s_magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(CompleteOffset), 1);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), writer.Length);
        checksum.CopyTo(header, HashOffset);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读取并完整校验保留快照，再将其投影为常驻查询索引。
    /// </summary>
    /// <param name="filePath">要读取的保留快照路径。</param>
    /// <param name="cancellationToken">取消当前读取的令牌。</param>
    /// <returns>含真实 GC 根类别及函数证据的查询索引。</returns>
    /// <exception cref="DiagnosticsException">格式、完成标记、长度或校验失败时引发。</exception>
    public static async Task<SnapshotIndex> ReadIndexAsync(string filePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length < HeaderLength || stream.Length > checked(HeaderLength + MaximumSnapshotBytes))
            {
                throw new InvalidDataException("保留快照长度无效。");
            }

            var header = new byte[HeaderLength];
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            if (!header.AsSpan(0, s_magic.Length).SequenceEqual(s_magic)
                || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8)) != FormatVersion
                || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(CompleteOffset)) != 1)
            {
                throw new InvalidDataException("保留快照不是已完成的受支持格式。");
            }

            var payloadLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16));
            if (payloadLength < 0 || payloadLength > MaximumSnapshotBytes || stream.Length != checked(HeaderLength + payloadLength))
            {
                throw new InvalidDataException("保留快照负载长度无效。");
            }

            await ValidatePayloadChecksumAsync(stream, payloadLength, header.AsMemory(HashOffset, Sha256HashLength), cancellationToken).ConfigureAwait(false);
            stream.Position = HeaderLength;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var reader = new PayloadReader(stream, payloadLength, hash, cancellationToken);
            var types = await ReadTypesAsync(reader).ConfigureAwait(false);
            var objects = await ReadObjectsAsync(reader, types).ConfigureAwait(false);
            var edges = await ReadEdgesAsync(reader).ConfigureAwait(false);
            var roots = await ReadRootsAsync(reader).ConfigureAwait(false);
            if (reader.Remaining != 0)
            {
                throw new InvalidDataException("保留快照包含未声明的尾部数据。");
            }

            var actualChecksum = hash.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(actualChecksum, header.AsSpan(HashOffset, actualChecksum.Length)))
            {
                throw new InvalidDataException("保留快照校验和不匹配。");
            }

            var groupedEdges = edges
                .GroupBy(edge => edge.SourceAddress)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<ulong>)group.Select(edge => edge.TargetAddress).Distinct().ToArray());
            return new SnapshotIndex(objects, groupedEdges, retentionRoots: roots);
        }
        catch (DiagnosticsException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or EndOfStreamException
                or OverflowException
                or ArgumentException)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.ProfilerCaptureFailed,
                "保留分析快照格式无效或文件已损坏。",
                exception);
        }
    }

    /// <summary>
    /// 仅验证保留快照的头部、完成标记、长度与完整负载校验和，不构造对象、边或根的托管索引。
    /// </summary>
    /// <param name="filePath">待验证的临时或已提升保留快照路径。</param>
    /// <param name="cancellationToken">取消当前顺序校验的令牌。</param>
    /// <returns>文件完整且为受支持完成格式时完成的任务。</returns>
    /// <exception cref="DiagnosticsException">格式、完成标记、长度或校验失败时引发。</exception>
    public static async Task ValidateAsync(string filePath, CancellationToken cancellationToken)
    {
        var validationDirectory = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? Path.GetTempPath(),
            $".retention-validate.{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(validationDirectory);
            var payload = await OpenVerifiedPayloadAsync(filePath, cancellationToken).ConfigureAwait(false);
            await using var stream = payload.Stream;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var reader = new PayloadReader(stream, payload.PayloadLength, hash, cancellationToken);
            var typeCount = await SkipTypesAsync(reader).ConfigureAwait(false);
            var objectCount = await reader.ReadInt32Async().ConfigureAwait(false);
            ValidateReadCount(objectCount, MaximumObjectCount, 20, reader.Remaining);
            var addressesUnsortedPath = Path.Combine(validationDirectory, "addresses.unsorted.bin");
            var addressesSortedPath = Path.Combine(validationDirectory, "addresses.sorted.bin");
            await using (var addressStream = new FileStream(addressesUnsortedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var addressWriter = new BinaryWriter(addressStream, Encoding.UTF8, leaveOpen: true))
            {
                for (var index = 0; index < objectCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var address = await reader.ReadUInt64Async().ConfigureAwait(false);
                    var typeIndex = await reader.ReadInt32Async().ConfigureAwait(false);
                    var size = await reader.ReadInt64Async().ConfigureAwait(false);
                    if (address == 0 || (uint)typeIndex >= (uint)typeCount || size < 0)
                    {
                        throw new InvalidDataException("保留快照对象记录无效。");
                    }

                    addressWriter.Write(address);
                    addressWriter.Write(index);
                }

                await addressStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await HeapIndexExternalSorter.SortAddressIdRecordsAsync(
                addressesUnsortedPath,
                addressesSortedPath,
                validationDirectory,
                HeapIndexResourcePolicy.GetExternalSortChunkBytes(),
                cancellationToken).ConfigureAwait(false);
            ValidateUniqueObjectAddresses(addressesSortedPath, objectCount, cancellationToken);
            using var addresses = new AddressObjectIdLookup(addressesSortedPath);

            var edgeCount = await reader.ReadInt32Async().ConfigureAwait(false);
            ValidateReadCount(edgeCount, MaximumEdgeCount, 16, reader.Remaining);
            for (var index = 0; index < edgeCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceAddress = await reader.ReadUInt64Async().ConfigureAwait(false);
                var targetAddress = await reader.ReadUInt64Async().ConfigureAwait(false);
                if (sourceAddress != 0 && !addresses.TryFind(sourceAddress, out _))
                {
                    throw new InvalidDataException("保留快照引用边包含未知非零源地址。");
                }

                if (targetAddress != 0 && !addresses.TryFind(targetAddress, out _))
                {
                    throw new InvalidDataException("保留快照引用边包含未知非零目标地址。");
                }
            }

            var rootCount = await reader.ReadInt32Async().ConfigureAwait(false);
            ValidateReadCount(rootCount, MaximumRootCount, 17, reader.Remaining);
            for (var index = 0; index < rootCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = await reader.ReadUInt64Async().ConfigureAwait(false);
                var kind = await reader.ReadByteAsync().ConfigureAwait(false);
                if (!Enum.IsDefined((MemoryRootKind)kind))
                {
                    throw new InvalidDataException("保留快照根类别无效。");
                }

                _ = await reader.ReadInt32Async().ConfigureAwait(false);
                await reader.ReadStringAsync(nullable: true).ConfigureAwait(false);
                await reader.ReadStringAsync(nullable: true).ConfigureAwait(false);
            }

            if (reader.Remaining != 0)
            {
                throw new InvalidDataException("保留快照包含未声明的尾部数据。");
            }
        }
        catch (DiagnosticsException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException or OverflowException or ArgumentException)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.ProfilerCaptureFailed,
                "保留分析快照格式无效或文件已损坏。",
                exception);
        }
        finally
        {
            HeapTemporaryArtifactCleanup.TryDeleteDirectory(validationDirectory);
        }
    }

    /// <summary>
    /// 预读取保留快照的对象和边计数，以便路由器在不构造完整对象图的情况下选择内存或磁盘索引。
    /// </summary>
    internal static async Task<RetentionSnapshotCounts> ReadCountsAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await OpenVerifiedPayloadAsync(filePath, cancellationToken).ConfigureAwait(false);
            await using var stream = payload.Stream;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var reader = new PayloadReader(stream, payload.PayloadLength, hash, cancellationToken);
            await SkipTypesAsync(reader).ConfigureAwait(false);
            var objectCount = await reader.ReadInt32Async().ConfigureAwait(false);
            ValidateReadCount(objectCount, MaximumObjectCount, 20, reader.Remaining);
            await reader.SkipAsync(checked((long)objectCount * 20)).ConfigureAwait(false);
            var edgeCount = await reader.ReadInt32Async().ConfigureAwait(false);
            ValidateReadCount(edgeCount, MaximumEdgeCount, 16, reader.Remaining);
            return new RetentionSnapshotCounts(objectCount, edgeCount);
        }
        catch (DiagnosticsException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException or OverflowException or ArgumentException)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.ProfilerCaptureFailed,
                "保留分析快照格式无效或文件已损坏。",
                exception);
        }
    }

    /// <summary>
    /// 直接从已验证的保留快照流写入基础索引工件；对象、地址、边和根证据只以受控大小的外排临时记录存在。
    /// </summary>
    internal static async Task<IReadOnlyList<string>> WriteMappedArtifactsAsync(
        string directory,
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = await OpenVerifiedPayloadAsync(filePath, cancellationToken).ConfigureAwait(false);
            await using var stream = payload.Stream;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var reader = new PayloadReader(stream, payload.PayloadLength, hash, cancellationToken);
            var sourceTypes = await ReadTypesAsync(reader).ConfigureAwait(false);
            var typeCatalog = CreateCanonicalTypeCatalog(sourceTypes);
            var objectCount = await reader.ReadInt32Async().ConfigureAwait(false);
            ValidateReadCount(objectCount, MaximumObjectCount, 20, reader.Remaining);

            var addressUnsortedPath = Path.Combine(directory, "address-to-id.unsorted.bin");
            var typeObjectUnsortedPath = Path.Combine(directory, "objects-by-type.unsorted.bin");
            await WriteObjectArtifactsAsync(
                directory,
                addressUnsortedPath,
                typeObjectUnsortedPath,
                reader,
                typeCatalog.Types,
                typeCatalog.SourceToCanonicalIndexes,
                objectCount,
                cancellationToken).ConfigureAwait(false);

            var chunkBytes = HeapIndexResourcePolicy.GetExternalSortChunkBytes();
            var addressPath = Path.Combine(directory, "address-to-id.bin");
            await HeapIndexExternalSorter.SortAddressIdRecordsAsync(
                addressUnsortedPath,
                addressPath,
                directory,
                chunkBytes,
                cancellationToken).ConfigureAwait(false);
            ValidateUniqueObjectAddresses(addressPath, objectCount, cancellationToken);
            File.Delete(addressUnsortedPath);

            var sortedTypeObjectsPath = Path.Combine(directory, "objects-by-type.sorted.bin");
            await HeapIndexExternalSorter.SortObjectIdEdgeRecordsAsync(
                typeObjectUnsortedPath,
                sortedTypeObjectsPath,
                directory,
                chunkBytes,
                sortByTarget: false,
                cancellationToken).ConfigureAwait(false);
            File.Delete(typeObjectUnsortedPath);
            await WriteObjectsByTypeAsync(Path.Combine(directory, "objects-by-type.bin"), sortedTypeObjectsPath, cancellationToken).ConfigureAwait(false);
            File.Delete(sortedTypeObjectsPath);
            await WriteTypeSummaryAsync(Path.Combine(directory, "type-summary.bin"), typeCatalog.Types, directory, cancellationToken).ConfigureAwait(false);

            var edgeCount = await reader.ReadInt32Async().ConfigureAwait(false);
            ValidateReadCount(edgeCount, MaximumEdgeCount, 16, reader.Remaining);
            var edgeUnsortedPath = Path.Combine(directory, "edges.unsorted.bin");
            await WriteEdgesAsync(
                reader,
                edgeCount,
                addressPath,
                edgeUnsortedPath,
                directory,
                chunkBytes,
                cancellationToken).ConfigureAwait(false);

            var rootCount = await reader.ReadInt32Async().ConfigureAwait(false);
            ValidateReadCount(rootCount, MaximumRootCount, 17, reader.Remaining);
            var rootPairsUnsortedPath = Path.Combine(directory, "roots.unsorted.bin");
            var rootValuesPath = Path.Combine(directory, "roots.values.bin");
            var rootValueOffsetsPath = Path.Combine(directory, "roots.value-offsets.bin");
            await WriteRootSpoolsAsync(
                reader,
                rootCount,
                addressPath,
                rootPairsUnsortedPath,
                rootValuesPath,
                rootValueOffsetsPath,
                cancellationToken).ConfigureAwait(false);
            if (reader.Remaining != 0)
            {
                throw new InvalidDataException("保留快照包含未声明的尾部数据。");
            }

            var actualChecksum = hash.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(actualChecksum, payload.Checksum))
            {
                throw new InvalidDataException("保留快照校验和不匹配。");
            }

            var forwardEdgesPath = Path.Combine(directory, "edges.forward.bin");
            var reverseEdgesPath = Path.Combine(directory, "edges.reverse.bin");
            await HeapIndexExternalSorter.SortObjectIdEdgeRecordsAsync(edgeUnsortedPath, forwardEdgesPath, directory, chunkBytes, sortByTarget: false, cancellationToken).ConfigureAwait(false);
            await HeapIndexExternalSorter.SortObjectIdEdgeRecordsAsync(edgeUnsortedPath, reverseEdgesPath, directory, chunkBytes, sortByTarget: true, cancellationToken).ConfigureAwait(false);
            await Task.Run(
                () => HeapIndexArtifactStore.WriteCsrFromSortedEdges(directory, "forward", forwardEdgesPath, objectCount, sortByTarget: false, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
            await Task.Run(
                () => HeapIndexArtifactStore.WriteCsrFromSortedEdges(directory, "reverse", reverseEdgesPath, objectCount, sortByTarget: true, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
            File.Delete(edgeUnsortedPath);
            File.Delete(forwardEdgesPath);
            File.Delete(reverseEdgesPath);

            var rootPairsSortedPath = Path.Combine(directory, "roots.sorted.bin");
            await HeapIndexExternalSorter.SortObjectIdEdgeRecordsAsync(rootPairsUnsortedPath, rootPairsSortedPath, directory, chunkBytes, sortByTarget: false, cancellationToken).ConfigureAwait(false);
            File.Delete(rootPairsUnsortedPath);
            await WriteRootEvidenceArtifactsAsync(
                directory,
                objectCount,
                rootPairsSortedPath,
                rootValuesPath,
                rootValueOffsetsPath,
                cancellationToken).ConfigureAwait(false);
            File.Delete(rootPairsSortedPath);
            File.Delete(rootValuesPath);
            File.Delete(rootValueOffsetsPath);

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
            throw new DiagnosticsException(
                DiagnosticsErrorCode.SnapshotIndexBuildFailed,
                "无法从保留分析快照构建磁盘堆索引。",
                exception);
        }
    }

    /// <summary>
    /// 打开并预先校验完整原始负载，随后将流定位到负载起点供一次受边界约束的流式索引构建使用。
    /// </summary>
    private static async Task<VerifiedPayload> OpenVerifiedPayloadAsync(string filePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (stream.Length < HeaderLength || stream.Length > checked(HeaderLength + MaximumSnapshotBytes))
            {
                throw new InvalidDataException("保留快照长度无效。");
            }

            var header = new byte[HeaderLength];
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            if (!header.AsSpan(0, s_magic.Length).SequenceEqual(s_magic)
                || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8)) != FormatVersion
                || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(CompleteOffset)) != 1)
            {
                throw new InvalidDataException("保留快照不是已完成的受支持格式。");
            }

            var payloadLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16));
            if (payloadLength < 0 || payloadLength > MaximumSnapshotBytes || stream.Length != checked(HeaderLength + payloadLength))
            {
                throw new InvalidDataException("保留快照负载长度无效。");
            }

            var checksum = header.AsSpan(HashOffset, Sha256HashLength).ToArray();
            await ValidatePayloadChecksumAsync(stream, payloadLength, checksum, cancellationToken).ConfigureAwait(false);
            stream.Position = HeaderLength;
            return new VerifiedPayload(stream, payloadLength, checksum);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 将类型表、对象元数据、地址查找外排记录和按类型对象外排记录顺序写出，期间不保留对象 DTO。
    /// </summary>
    private static async Task WriteObjectArtifactsAsync(
        string directory,
        string addressUnsortedPath,
        string typeObjectUnsortedPath,
        PayloadReader reader,
        TypeIdentity[] types,
        int[] sourceToCanonicalIndexes,
        int objectCount,
        CancellationToken cancellationToken)
    {
        await using (var typeStream = new FileStream(Path.Combine(directory, "types.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var typeWriter = new BinaryWriter(typeStream, Encoding.UTF8, leaveOpen: true))
        {
            typeWriter.Write(types.Length);
            foreach (var type in types)
            {
                typeWriter.Write(type.TypeName);
                typeWriter.Write(type.AssemblyName ?? string.Empty);
            }

            await typeStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var objectStream = new FileStream(Path.Combine(directory, "objects.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var addressStream = new FileStream(addressUnsortedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var typeObjectStream = new FileStream(typeObjectUnsortedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var objects = new BinaryWriter(objectStream, Encoding.UTF8, leaveOpen: true);
        using var addresses = new BinaryWriter(addressStream, Encoding.UTF8, leaveOpen: true);
        using var typeObjects = new BinaryWriter(typeObjectStream, Encoding.UTF8, leaveOpen: true);
        for (var objectId = 0; objectId < objectCount; objectId++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var address = await reader.ReadUInt64Async().ConfigureAwait(false);
            var sourceTypeIndex = await reader.ReadInt32Async().ConfigureAwait(false);
            var size = await reader.ReadInt64Async().ConfigureAwait(false);
            if (address == 0 || (uint)sourceTypeIndex >= (uint)sourceToCanonicalIndexes.Length || size < 0)
            {
                throw new InvalidDataException("保留快照对象记录无效。");
            }

            var typeIndex = sourceToCanonicalIndexes[sourceTypeIndex];
            objects.Write(address);
            objects.Write(typeIndex);
            objects.Write(size);
            addresses.Write(address);
            addresses.Write(objectId);
            typeObjects.Write(typeIndex);
            typeObjects.Write(objectId);
        }

        await objectStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await addressStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await typeObjectStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按完整 <see cref="TypeIdentity"/> 首次出现顺序建立规范类型目录，并生成原始类型索引到规范索引的致密映射。
    /// </summary>
    /// <param name="sourceTypes">保留快照中的原始类型表，允许不同原始索引描述同一类型身份。</param>
    /// <returns>去重后的类型目录及每个原始类型索引对应的规范索引。</returns>
    private static CanonicalTypeCatalog CreateCanonicalTypeCatalog(TypeIdentity[] sourceTypes)
    {
        var canonicalTypes = new List<TypeIdentity>(sourceTypes.Length);
        var canonicalIndexes = new Dictionary<TypeIdentity, int>(sourceTypes.Length);
        var sourceToCanonicalIndexes = new int[sourceTypes.Length];
        for (var sourceIndex = 0; sourceIndex < sourceTypes.Length; sourceIndex++)
        {
            var type = sourceTypes[sourceIndex];
            if (!canonicalIndexes.TryGetValue(type, out var canonicalIndex))
            {
                canonicalIndex = canonicalTypes.Count;
                canonicalIndexes.Add(type, canonicalIndex);
                canonicalTypes.Add(type);
            }

            sourceToCanonicalIndexes[sourceIndex] = canonicalIndex;
        }

        return new CanonicalTypeCatalog(canonicalTypes.ToArray(), sourceToCanonicalIndexes);
    }

    /// <summary>
    /// 将按类型排序的 <c>typeId/objectId</c> 临时记录投影为只有 objectId 的对象分页工件。
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
    /// 从 Profiler 原始记录 spool 流式写入保留快照；对象、边和根不会重新汇总为全量托管 DTO。
    /// </summary>
    /// <param name="filePath">不存在的临时保留快照目标路径。</param>
    /// <param name="spool">拥有固定宽度原始记录和有限证据集合的捕获 spool。</param>
    /// <param name="cancellationToken">取消当前转换和文件写入的令牌。</param>
    /// <returns>代表完成校验和、完成标记和文件刷新操作的任务。</returns>
    /// <exception cref="DiagnosticsException">记录数量、输出大小或存储格式限制无效时引发。</exception>
    internal static async Task WriteFromProfilerSpoolAsync(
        string filePath,
        RetentionProfilerRawCaptureSpool spool,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(spool);
        if (File.Exists(filePath))
        {
            throw new IOException("保留快照临时文件已存在。");
        }

        var sortedObjectsPath = Path.Combine(spool.DirectoryPath, "objects.by-class.bin");
        var objectIdSortedPath = Path.Combine(spool.DirectoryPath, "objects.by-id.bin");
        var uniqueObjectsPath = Path.Combine(spool.DirectoryPath, "objects.unique.bin");
        try
        {
            await HeapIndexExternalSorter.SortProfilerObjectRecordsByObjectIdAsync(
                spool.ObjectPath,
                objectIdSortedPath,
                spool.DirectoryPath,
                HeapIndexResourcePolicy.GetExternalSortChunkBytes(),
                cancellationToken).ConfigureAwait(false);
            await DeduplicateProfilerObjectsAsync(objectIdSortedPath, uniqueObjectsPath, cancellationToken).ConfigureAwait(false);
            await HeapIndexExternalSorter.SortProfilerObjectRecordsByClassIdAsync(
                uniqueObjectsPath,
                sortedObjectsPath,
                spool.DirectoryPath,
                HeapIndexResourcePolicy.GetExternalSortChunkBytes(),
                cancellationToken).ConfigureAwait(false);
            var counts = await ScanProfilerObjectLayoutAsync(sortedObjectsPath, cancellationToken).ConfigureAwait(false);
            ValidateCount(counts.TypeCount, MaximumTypeCount, "Profiler 类型数");
            ValidateCount(counts.ObjectCount, MaximumObjectCount, "Profiler 对象数");
            ValidateCount(ToSupportedCount(spool.EdgeCount, "Profiler 边数"), MaximumEdgeCount, "Profiler 边数");
            ValidateCount(ToSupportedCount(spool.RootCount, "Profiler 根数"), MaximumRootCount, "Profiler 根数");

            var verifiedTypes = spool.Types
                .Where(type => type.ClassId != 0 && !string.IsNullOrWhiteSpace(type.TypeName))
                .GroupBy(type => type.ClassId)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(type => type.TypeName, StringComparer.Ordinal).First());
            await using var stream = new FileStream(
                filePath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await stream.WriteAsync(new byte[HeaderLength], cancellationToken).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var writer = new PayloadWriter(stream, hash, cancellationToken);
            await WriteProfilerTypesAsync(writer, sortedObjectsPath, verifiedTypes, counts.TypeCount, cancellationToken).ConfigureAwait(false);
            await WriteProfilerObjectsAsync(writer, sortedObjectsPath, counts.ObjectCount, cancellationToken).ConfigureAwait(false);
            await WriteProfilerEdgesAsync(writer, spool.EdgePath, ToSupportedCount(spool.EdgeCount, "Profiler 边数"), cancellationToken).ConfigureAwait(false);
            await WriteProfilerRootsAsync(writer, spool.RootPath, ToSupportedCount(spool.RootCount, "Profiler 根数"), spool.Functions, cancellationToken).ConfigureAwait(false);

            await writer.FlushAsync().ConfigureAwait(false);
            var checksum = hash.GetHashAndReset();
            stream.Position = 0;
            var header = new byte[HeaderLength];
            s_magic.CopyTo(header, 0);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), FormatVersion);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(CompleteOffset), 1);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), writer.Length);
            checksum.CopyTo(header, HashOffset);
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DiagnosticsException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OverflowException or UnauthorizedAccessException)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.ProfilerCaptureFailed,
                "无法将 Profiler 原始记录转换为保留快照。",
                exception);
        }
        finally
        {
            HeapTemporaryArtifactCleanup.TryDeleteFile(objectIdSortedPath);
            HeapTemporaryArtifactCleanup.TryDeleteFile(uniqueObjectsPath);
            HeapTemporaryArtifactCleanup.TryDeleteFile(sortedObjectsPath);
        }
    }

    /// <summary>
    /// 按对象地址排序的 Profiler 记录流式去重；每个对象仅保留首条确定性记录，避免构造全量地址集合。
    /// </summary>
    private static async Task DeduplicateProfilerObjectsAsync(
        string sortedPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        using var reader = new BinaryReader(File.OpenRead(sortedPath), Encoding.UTF8, leaveOpen: false);
        ulong? previousObjectId = null;
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = ReadProfilerObject(reader);
            if (record.ObjectId == 0 || previousObjectId == record.ObjectId)
            {
                continue;
            }

            previousObjectId = record.ObjectId;
            writer.Write(record.ObjectId);
            writer.Write(record.ClassId);
            writer.Write(record.SizeBytes);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 扫描 ClassID 排序后的对象 spool，计算有效对象数和实际需要写出的类型组数。
    /// </summary>
    private static Task<ProfilerObjectLayoutCounts> ScanProfilerObjectLayoutAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var typeCount = 0;
            var objectCount = 0;
            ulong? previousClassId = null;
            using var reader = new BinaryReader(File.OpenRead(path));
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = ReadProfilerObject(reader);
                if (record.ObjectId == 0)
                {
                    continue;
                }

                objectCount = checked(objectCount + 1);
                if (previousClassId != record.ClassId)
                {
                    previousClassId = record.ClassId;
                    typeCount = checked(typeCount + 1);
                }
            }

            return new ProfilerObjectLayoutCounts(typeCount, objectCount);
        }, CancellationToken.None);

    /// <summary>
    /// 写出经排序对象所需的类型表；类型身份只能来自已验证 ClassID 证据或明确的未知占位。
    /// </summary>
    private static async Task WriteProfilerTypesAsync(
        PayloadWriter writer,
        string sortedObjectsPath,
        IReadOnlyDictionary<nuint, RetentionProfilerRawType> verifiedTypes,
        int expectedTypeCount,
        CancellationToken cancellationToken)
    {
        await writer.WriteInt32Async(expectedTypeCount).ConfigureAwait(false);
        ulong? previousClassId = null;
        var written = 0;
        using var reader = new BinaryReader(File.OpenRead(sortedObjectsPath));
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = ReadProfilerObject(reader);
            if (record.ObjectId == 0 || previousClassId == record.ClassId)
            {
                continue;
            }

            previousClassId = record.ClassId;
            var type = RetentionProfilerSnapshotConverter.CreateTypeIdentity(
                unchecked((nuint)record.ClassId),
                verifiedTypes);
            await writer.WriteStringAsync(type.TypeName).ConfigureAwait(false);
            await writer.WriteNullableStringAsync(type.AssemblyName).ConfigureAwait(false);
            written = checked(written + 1);
        }

        if (written != expectedTypeCount)
        {
            throw new InvalidDataException("Profiler 类型 spool 与预扫描结果不一致。");
        }
    }

    /// <summary>
    /// 写出按 ClassID 分组的对象记录；每个类型组的连续索引与前序类型表完全一致。
    /// </summary>
    private static async Task WriteProfilerObjectsAsync(
        PayloadWriter writer,
        string sortedObjectsPath,
        int expectedObjectCount,
        CancellationToken cancellationToken)
    {
        await writer.WriteInt32Async(expectedObjectCount).ConfigureAwait(false);
        ulong? previousClassId = null;
        var typeIndex = -1;
        var written = 0;
        using var reader = new BinaryReader(File.OpenRead(sortedObjectsPath));
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = ReadProfilerObject(reader);
            if (record.ObjectId == 0)
            {
                continue;
            }

            if (previousClassId != record.ClassId)
            {
                previousClassId = record.ClassId;
                typeIndex = checked(typeIndex + 1);
            }

            var size = record.SizeBytes <= uint.MaxValue ? checked((long)record.SizeBytes) : 0L;
            await writer.WriteObjectRecordAsync(record.ObjectId, typeIndex, size).ConfigureAwait(false);
            written = checked(written + 1);
        }

        if (written != expectedObjectCount)
        {
            throw new InvalidDataException("Profiler 对象 spool 与预扫描结果不一致。");
        }
    }

    /// <summary>
    /// 顺序写出原始引用边；索引构建只忽略零地址，并对未知非零端点稳定失败，避免此阶段建立地址集合。
    /// </summary>
    private static async Task WriteProfilerEdgesAsync(
        PayloadWriter writer,
        string path,
        int expectedEdgeCount,
        CancellationToken cancellationToken)
    {
        await writer.WriteInt32Async(expectedEdgeCount).ConfigureAwait(false);
        using var reader = new BinaryReader(File.OpenRead(path));
        for (var index = 0; index < expectedEdgeCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteEdgeRecordAsync(reader.ReadUInt64(), reader.ReadUInt64()).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 顺序写出根记录，并仅在函数索引和 FunctionID 经验证匹配时写入栈根函数证据。
    /// </summary>
    private static async Task WriteProfilerRootsAsync(
        PayloadWriter writer,
        string path,
        int expectedRootCount,
        IReadOnlyList<RetentionProfilerRawFunction> functions,
        CancellationToken cancellationToken)
    {
        await writer.WriteInt32Async(expectedRootCount).ConfigureAwait(false);
        using var reader = new BinaryReader(File.OpenRead(path));
        for (var index = 0; index < expectedRootCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rawRoot = new RetentionProfilerRawRoot(
                unchecked((nuint)reader.ReadUInt64()),
                reader.ReadUInt32(),
                reader.ReadUInt32(),
                unchecked((nuint)reader.ReadUInt64()),
                reader.ReadUInt32(),
                reader.ReadUInt32());
            var root = RetentionProfilerSnapshotConverter.CreateRoot(rawRoot, functions);
            await writer.WriteRootRecordAsync((ulong)rawRoot.ObjectId, root).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 读取一个 ClassID 排序的固定宽度 Profiler 对象记录。
    /// </summary>
    private static ProfilerObjectSpoolRecord ReadProfilerObject(BinaryReader reader) =>
        new(reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64());

    /// <summary>
    /// 将长度计数转换为当前保留快照格式支持的整数范围。
    /// </summary>
    private static int ToSupportedCount(long count, string name)
    {
        if (count < 0 || count > int.MaxValue)
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.SnapshotStorageLimitReached, $"{name} 超过保留快照格式可表示范围。");
        }

        return checked((int)count);
    }

    /// <summary>
    /// 表示来自 ClassID 排序 spool 的单个固定宽度对象记录。
    /// </summary>
    private readonly record struct ProfilerObjectSpoolRecord(ulong ObjectId, ulong ClassId, ulong SizeBytes);

    /// <summary>
    /// 表示写入前从排序对象 spool 得到的类型组和有效对象计数。
    /// </summary>
    private readonly record struct ProfilerObjectLayoutCounts(int TypeCount, int ObjectCount);

    /// <summary>
    /// 保存映射工件使用的规范类型表及原始类型索引到规范索引的转换表。
    /// </summary>
    /// <param name="Types">按首次出现顺序去重的完整类型身份。</param>
    /// <param name="SourceToCanonicalIndexes">与原始类型表等长的规范索引映射。</param>
    private readonly record struct CanonicalTypeCatalog(
        TypeIdentity[] Types,
        int[] SourceToCanonicalIndexes);

    /// <summary>
    /// 顺序扫描固定宽度对象工件生成非零类型的统计，避免在构建阶段保留每个对象或按类型对象集合。
    /// </summary>
    private static async Task WriteTypeSummaryAsync(
        string outputPath,
        TypeIdentity[] types,
        string directory,
        CancellationToken cancellationToken)
    {
        var counts = new long[types.Length];
        var sizes = new long[types.Length];
        using (var reader = new BinaryReader(File.OpenRead(Path.Combine(directory, "objects.bin")), Encoding.UTF8, leaveOpen: false))
        {
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = reader.ReadUInt64();
                var typeIndex = reader.ReadInt32();
                var size = reader.ReadInt64();
                if ((uint)typeIndex >= (uint)types.Length || size < 0)
                {
                    throw new InvalidDataException("对象工件类型或大小无效。");
                }

                counts[typeIndex] = checked(counts[typeIndex] + 1);
                sizes[typeIndex] = checked(sizes[typeIndex] + size);
            }
        }

        var values = types
            .Select((type, index) => new MemoryTypeSummary(type, counts[index], sizes[index]))
            .Where(summary => summary.ObjectCount > 0)
            .OrderByDescending(summary => summary.TotalSizeBytes)
            .ThenBy(summary => summary.Type.TypeName, StringComparer.Ordinal)
            .ToArray();
        await using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(stream, values, cancellationToken: cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 使用两次外排排序和顺序归并将保留快照地址边转换为连续 objectId 边；
    /// 零端点按空引用忽略，未知非零端点稳定失败，且高边密度图不会为每条边执行随机地址二分查找。
    /// </summary>
    private static async Task WriteEdgesAsync(
        PayloadReader reader,
        int edgeCount,
        string addressIndexPath,
        string outputPath,
        string workingDirectory,
        int chunkBytes,
        CancellationToken cancellationToken)
    {
        var addressPairsUnsortedPath = Path.Combine(workingDirectory, "edges-address.unsorted.bin");
        var sourceAddressSortedPath = Path.Combine(workingDirectory, "edges-address.source-sorted.bin");
        var targetAddressWithSourceIdPath = Path.Combine(workingDirectory, "edges-target-address.source-id.unsorted.bin");
        var targetAddressWithSourceIdSortedPath = Path.Combine(workingDirectory, "edges-target-address.source-id.sorted.bin");
        try
        {
            await WriteAddressPairsAsync(reader, edgeCount, addressPairsUnsortedPath, cancellationToken).ConfigureAwait(false);
            await HeapIndexExternalSorter.SortAddressPairRecordsAsync(
                addressPairsUnsortedPath,
                sourceAddressSortedPath,
                workingDirectory,
                chunkBytes,
                sortBySecondAddress: false,
                cancellationToken).ConfigureAwait(false);
            await WriteTargetAddressesWithSourceObjectIdsAsync(
                sourceAddressSortedPath,
                addressIndexPath,
                targetAddressWithSourceIdPath,
                cancellationToken).ConfigureAwait(false);
            await HeapIndexExternalSorter.SortAddressIdRecordsAsync(
                targetAddressWithSourceIdPath,
                targetAddressWithSourceIdSortedPath,
                workingDirectory,
                chunkBytes,
                cancellationToken).ConfigureAwait(false);
            await WriteObjectIdEdgesAsync(
                targetAddressWithSourceIdSortedPath,
                addressIndexPath,
                outputPath,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteIfExists(addressPairsUnsortedPath);
            DeleteIfExists(sourceAddressSortedPath);
            DeleteIfExists(targetAddressWithSourceIdPath);
            DeleteIfExists(targetAddressWithSourceIdSortedPath);
        }
    }

    /// <summary>
    /// 将原始负载中的固定宽度源/目标地址边顺序写入外排文件；该阶段只消费负载，不进行地址查找。
    /// </summary>
    private static async Task WriteAddressPairsAsync(
        PayloadReader reader,
        int edgeCount,
        string outputPath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        for (var index = 0; index < edgeCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Write(await reader.ReadUInt64Async().ConfigureAwait(false));
            writer.Write(await reader.ReadUInt64Async().ConfigureAwait(false));
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 将按源地址排序的边与按地址排序的对象工件顺序归并，写出 <c>targetAddress + sourceObjectId</c> 记录。
    /// 两个输入只前进不回退，避免为每条边随机定位地址索引。
    /// </summary>
    private static async Task WriteTargetAddressesWithSourceObjectIdsAsync(
        string sourceAddressSortedPath,
        string addressIndexPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        using var pairs = new BinaryReader(OpenSequentialRead(sourceAddressSortedPath), Encoding.UTF8, leaveOpen: false);
        using var addresses = new BinaryReader(OpenSequentialRead(addressIndexPath), Encoding.UTF8, leaveOpen: false);
        await using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        var hasAddress = TryReadAddressObjectId(addresses, out var currentAddress, out var currentObjectId);
        while (TryReadAddressPair(pairs, out var sourceAddress, out var targetAddress))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceAddress == 0)
            {
                continue;
            }
            while (hasAddress && currentAddress < sourceAddress)
            {
                hasAddress = TryReadAddressObjectId(addresses, out currentAddress, out currentObjectId);
            }

            if (hasAddress && currentAddress == sourceAddress)
            {
                writer.Write(targetAddress);
                writer.Write(currentObjectId);
                continue;
            }

            throw new InvalidDataException("保留快照引用边包含未知非零源地址。");
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 将按目标地址排序的 <c>targetAddress + sourceObjectId</c> 记录与对象地址工件顺序归并，
    /// 生成后续 CSR 构建使用的 <c>sourceObjectId + targetObjectId</c> 边记录。
    /// </summary>
    private static async Task WriteObjectIdEdgesAsync(
        string targetAddressWithSourceIdSortedPath,
        string addressIndexPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        using var sourceIds = new BinaryReader(OpenSequentialRead(targetAddressWithSourceIdSortedPath), Encoding.UTF8, leaveOpen: false);
        using var addresses = new BinaryReader(OpenSequentialRead(addressIndexPath), Encoding.UTF8, leaveOpen: false);
        await using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        var hasAddress = TryReadAddressObjectId(addresses, out var currentAddress, out var currentObjectId);
        while (TryReadAddressObjectId(sourceIds, out var targetAddress, out var sourceObjectId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (targetAddress == 0)
            {
                continue;
            }
            while (hasAddress && currentAddress < targetAddress)
            {
                hasAddress = TryReadAddressObjectId(addresses, out currentAddress, out currentObjectId);
            }

            if (hasAddress && currentAddress == targetAddress)
            {
                writer.Write(sourceObjectId);
                writer.Write(currentObjectId);
                continue;
            }

            throw new InvalidDataException("保留快照引用边包含未知非零目标地址。");
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 顺序验证已排序地址工件严格递增且记录数准确，避免重复地址被后续归并静默绑定到任意对象 ID。
    /// </summary>
    /// <param name="addressIndexPath">按地址排序的 <c>address + objectId</c> 固定宽度工件。</param>
    /// <param name="expectedObjectCount">原始快照声明的对象数。</param>
    /// <param name="cancellationToken">取消顺序验证的令牌。</param>
    /// <exception cref="InvalidDataException">记录数错误、地址为零或地址不严格递增时引发。</exception>
    private static void ValidateUniqueObjectAddresses(
        string addressIndexPath,
        int expectedObjectCount,
        CancellationToken cancellationToken)
    {
        using var reader = new BinaryReader(OpenSequentialRead(addressIndexPath), Encoding.UTF8, leaveOpen: false);
        if (reader.BaseStream.Length != checked((long)expectedObjectCount * (sizeof(ulong) + sizeof(int))))
        {
            throw new InvalidDataException("保留快照地址索引记录数无效。");
        }

        var previousAddress = 0UL;
        for (var index = 0; index < expectedObjectCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var address = reader.ReadUInt64();
            _ = reader.ReadInt32();
            if (address == 0 || (index > 0 && address <= previousAddress))
            {
                throw new InvalidDataException("保留快照对象地址重复或排序无效。");
            }
            previousAddress = address;
        }
    }

    /// <summary>
    /// 以顺序扫描提示打开已完成的固定宽度工件，避免归并阶段出现随机访问。
    /// </summary>
    private static FileStream OpenSequentialRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        64 * 1024,
        FileOptions.SequentialScan);

    /// <summary>
    /// 从固定宽度源/目标地址边文件读取一条完整记录；截断文件会稳定失败而非静默丢边。
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
            throw new InvalidDataException("地址对外排记录截断。");
        }

        firstAddress = reader.ReadUInt64();
        secondAddress = reader.ReadUInt64();
        return true;
    }

    /// <summary>
    /// 从固定宽度地址/objectId 工件读取一条完整记录；该格式同时用于地址索引和中间映射记录。
    /// </summary>
    private static bool TryReadAddressObjectId(BinaryReader reader, out ulong address, out int objectId)
    {
        if (reader.BaseStream.Position == reader.BaseStream.Length)
        {
            address = default;
            objectId = default;
            return false;
        }

        if (reader.BaseStream.Length - reader.BaseStream.Position < sizeof(ulong) + sizeof(int))
        {
            throw new InvalidDataException("地址对象标识外排记录截断。");
        }

        address = reader.ReadUInt64();
        objectId = reader.ReadInt32();
        return true;
    }

    /// <summary>
    /// 删除本次边映射创建的临时文件；文件尚未创建或已被上游清理时无需干预。
    /// </summary>
    private static void DeleteIfExists(string path)
    {
        HeapTemporaryArtifactCleanup.TryDeleteFile(path);
    }

    /// <summary>
    /// 将根记录转换为 objectId/序号外排对与变长根值流；弱根只消费原始记录，不写入存活证据工件。
    /// </summary>
    private static async Task WriteRootSpoolsAsync(
        PayloadReader reader,
        int rootCount,
        string addressIndexPath,
        string rootPairsPath,
        string rootValuesPath,
        string rootValueOffsetsPath,
        CancellationToken cancellationToken)
    {
        using var addresses = new AddressObjectIdLookup(addressIndexPath);
        await using var pairStream = new FileStream(rootPairsPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var valueStream = new FileStream(rootValuesPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var offsetStream = new FileStream(rootValueOffsetsPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var pairs = new BinaryWriter(pairStream, Encoding.UTF8, leaveOpen: true);
        using var values = new BinaryWriter(valueStream, Encoding.UTF8, leaveOpen: true);
        using var offsets = new BinaryWriter(offsetStream, Encoding.UTF8, leaveOpen: true);
        var ordinal = 0;
        for (var index = 0; index < rootCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var address = await reader.ReadUInt64Async().ConfigureAwait(false);
            var kindValue = await reader.ReadByteAsync().ConfigureAwait(false);
            var flagsValue = await reader.ReadInt32Async().ConfigureAwait(false);
            if (!Enum.IsDefined((MemoryRootKind)kindValue))
            {
                throw new InvalidDataException("保留快照根类别无效。");
            }

            var root = new MemoryRetentionRoot(
                (MemoryRootKind)kindValue,
                (MemoryRootFlags)flagsValue,
                await reader.ReadStringAsync(nullable: true).ConfigureAwait(false),
                await reader.ReadStringAsync(nullable: true).ConfigureAwait(false));
            if (root.Flags.HasFlag(MemoryRootFlags.WeakReference) || !addresses.TryFind(address, out var objectId))
            {
                continue;
            }

            offsets.Write(valueStream.Position);
            WriteRootValue(values, root);
            pairs.Write(objectId);
            pairs.Write(ordinal);
            ordinal++;
        }

        offsets.Write(valueStream.Position);
        await pairStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await valueStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await offsetStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 基于排序后的 objectId/值序号对生成最终根偏移表和根证据值流；只复制当前对象命中的变长记录。
    /// </summary>
    private static async Task WriteRootEvidenceArtifactsAsync(
        string directory,
        int objectCount,
        string sortedPairsPath,
        string valuesPath,
        string valueOffsetsPath,
        CancellationToken cancellationToken)
    {
        await using var finalOffsetStream = new FileStream(Path.Combine(directory, "roots-by-object.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var finalValueStream = new FileStream(Path.Combine(directory, "root-evidence.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var finalOffsets = new BinaryWriter(finalOffsetStream, Encoding.UTF8, leaveOpen: true);
        using var pairs = new BinaryReader(File.OpenRead(sortedPairsPath), Encoding.UTF8, leaveOpen: false);
        using var offsets = new BinaryReader(File.OpenRead(valueOffsetsPath), Encoding.UTF8, leaveOpen: false);
        await using var values = new FileStream(valuesPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        var valueCount = checked((int)(offsets.BaseStream.Length / sizeof(long) - 1));
        if (offsets.BaseStream.Length != checked(((long)valueCount + 1) * sizeof(long)))
        {
            throw new InvalidDataException("根值偏移工件长度无效。");
        }

        var hasPair = TryReadObjectIdPair(pairs, out var pendingObjectId, out var pendingOrdinal);
        var copyBuffer = new byte[64 * 1024];
        for (var objectId = 0; objectId < objectCount; objectId++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            finalOffsets.Write(finalValueStream.Position);
            while (hasPair && pendingObjectId == objectId)
            {
                if (pendingOrdinal < 0 || pendingOrdinal >= valueCount)
                {
                    throw new InvalidDataException("根值序号超出范围。");
                }

                offsets.BaseStream.Position = (long)pendingOrdinal * sizeof(long);
                var start = offsets.ReadInt64();
                var end = offsets.ReadInt64();
                if (start < 0 || end < start || end > values.Length)
                {
                    throw new InvalidDataException("根值偏移无效。");
                }

                values.Position = start;
                var remaining = end - start;
                while (remaining > 0)
                {
                    var requested = (int)Math.Min(copyBuffer.Length, remaining);
                    var read = await values.ReadAsync(copyBuffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new InvalidDataException("根值工件截断。");
                    }

                    await finalValueStream.WriteAsync(copyBuffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    remaining -= read;
                }

                hasPair = TryReadObjectIdPair(pairs, out pendingObjectId, out pendingOrdinal);
            }
        }

        if (hasPair)
        {
            throw new InvalidDataException("根对象标识超出对象范围。");
        }

        finalOffsets.Write(finalValueStream.Position);
        await finalOffsetStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await finalValueStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 写入与映射根证据读取器一致的变长根值记录。
    /// </summary>
    private static void WriteRootValue(BinaryWriter writer, MemoryRetentionRoot root)
    {
        writer.Write((byte)root.Kind);
        writer.Write((int)root.Flags);
        WriteNullableString(writer, root.FunctionName);
        WriteNullableString(writer, root.ModuleName);
    }

    /// <summary>
    /// 写入 -1 表示 null 的 UTF-8 字符串，保持根函数证据的缺失语义。
    /// </summary>
    private static void WriteNullableString(BinaryWriter writer, string? value)
    {
        if (value is null)
        {
            writer.Write(-1);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    /// <summary>
    /// 尝试读取外排 objectId/辅助编号记录，EOF 必须位于完整固定宽度记录边界。
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
            throw new InvalidDataException("排序对象标识记录截断。");
        }

        objectId = reader.ReadInt32();
        value = reader.ReadInt32();
        return true;
    }

    /// <summary>
    /// 在排序后的固定宽度地址工件上执行二分查找，不创建地址到对象标识的托管字典。
    /// </summary>
    private sealed class AddressObjectIdLookup : IDisposable
    {
        private const int RecordBytes = sizeof(ulong) + sizeof(int);
        private readonly FileStream _stream;
        private readonly BinaryReader _reader;

        /// <summary>
        /// 打开并验证地址索引文件。
        /// </summary>
        public AddressObjectIdLookup(string path)
        {
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
            if (_stream.Length % RecordBytes != 0)
            {
                _stream.Dispose();
                throw new InvalidDataException("地址索引工件长度无效。");
            }

            _reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
        }

        /// <summary>
        /// 查找地址对应的连续对象标识；地址不存在时返回 <see langword="false"/>。
        /// </summary>
        public bool TryFind(ulong address, out int objectId)
        {
            var low = 0L;
            var high = _stream.Length / RecordBytes - 1;
            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                _stream.Position = middle * RecordBytes;
                var candidate = _reader.ReadUInt64();
                var id = _reader.ReadInt32();
                if (candidate == address)
                {
                    objectId = id;
                    return true;
                }

                if (candidate < address)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            objectId = default;
            return false;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _reader.Dispose();
            _stream.Dispose();
        }
    }

    /// <summary>
    /// 表示经过原始负载校验后仍由调用方负责释放的文件流与其声明负载边界。
    /// </summary>
    private sealed record VerifiedPayload(FileStream Stream, long PayloadLength, byte[] Checksum);

    /// <summary>
    /// 表示路由决策所需的对象和边计数。
    /// </summary>
    internal readonly record struct RetentionSnapshotCounts(int ObjectCount, int EdgeCount);

    /// <summary>
    /// 在分配类型、对象、边和根数组前流式校验完整负载，防止受损大文件先触发巨量托管分配。
    /// </summary>
    private static async Task ValidatePayloadChecksumAsync(
        FileStream stream,
        long payloadLength,
        ReadOnlyMemory<byte> expectedChecksum,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        var remaining = payloadLength;
        while (remaining > 0)
        {
            var requested = (int)Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("保留快照负载提前结束。");
            }

            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }

        var actualChecksum = hash.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(actualChecksum, expectedChecksum.Span))
        {
            throw new InvalidDataException("保留快照校验和不匹配。");
        }
    }

    /// <summary>
    /// 判断指定文件是否拥有本格式的固定魔数；完整校验由读取方法完成。
    /// </summary>
    /// <param name="filePath">待判断的文件路径。</param>
    /// <returns>文件头含有保留快照魔数时返回 <see langword="true"/>。</returns>
    public static bool HasMagic(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            Span<byte> magic = stackalloc byte[8];
            return stream.Read(magic) == magic.Length && magic.SequenceEqual(s_magic);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 表示待写入保留快照的去重类型表、对象表、边表和根表。
    /// </summary>
    internal sealed record SnapshotData(
        IReadOnlyList<TypeIdentity> Types,
        IReadOnlyList<ObjectRecord> Objects,
        IReadOnlyList<EdgeRecord> Edges,
        IReadOnlyList<RootRecord> Roots);

    /// <summary>
    /// 表示对象地址、其类型表索引和对象大小。
    /// </summary>
    internal readonly record struct ObjectRecord(ulong Address, int TypeIndex, long SizeBytes);

    /// <summary>
    /// 表示一条对象地址间的有向引用边。
    /// </summary>
    internal readonly record struct EdgeRecord(ulong SourceAddress, ulong TargetAddress);

    /// <summary>
    /// 表示一个对象地址及其从 CLR 回调得到的 GC 根证据。
    /// </summary>
    internal readonly record struct RootRecord(ulong ObjectAddress, MemoryRetentionRoot Root);

    /// <summary>
    /// 校验写入前的集合数量，避免在格式写入一半后才发现超出协议上限。
    /// </summary>
    private static void ValidateDataCounts(SnapshotData data)
    {
        ValidateCount(data.Types.Count, MaximumTypeCount, nameof(data.Types));
        ValidateCount(data.Objects.Count, MaximumObjectCount, nameof(data.Objects));
        ValidateCount(data.Edges.Count, MaximumEdgeCount, nameof(data.Edges));
        ValidateCount(data.Roots.Count, MaximumRootCount, nameof(data.Roots));
    }

    /// <summary>
    /// 校验单个对象记录的类型索引与大小约束。
    /// </summary>
    private static void ValidateObject(ObjectRecord item, int typeCount)
    {
        if (item.Address == 0 || item.TypeIndex < 0 || item.TypeIndex >= typeCount || item.SizeBytes < 0)
        {
            throw new InvalidDataException("保留快照对象记录无效。");
        }
    }

    /// <summary>
    /// 按固定上限校验负载集合计数。
    /// </summary>
    private static void ValidateCount(int value, int maximum, string parameterName)
    {
        if (value < 0 || value > maximum)
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.SnapshotStorageLimitReached, "保留分析快照超过支持的格式容量。");
        }
    }

    /// <summary>
    /// 异步读取类型表，并在构造字符串前验证每个长度。
    /// </summary>
    private static async Task<TypeIdentity[]> ReadTypesAsync(PayloadReader reader)
    {
        var count = await reader.ReadInt32Async().ConfigureAwait(false);
        ValidateReadCount(count, MaximumTypeCount, sizeof(int), reader.Remaining);
        var types = new TypeIdentity[count];
        for (var index = 0; index < types.Length; index++)
        {
            var name = await reader.ReadStringAsync(nullable: false).ConfigureAwait(false)
                ?? throw new InvalidDataException("类型名不能为空。");
            var assembly = await reader.ReadStringAsync(nullable: true).ConfigureAwait(false);
            types[index] = new TypeIdentity(name, assembly);
        }

        return types;
    }

    /// <summary>
    /// 跳过类型表但仍校验其计数和字符串边界，供路由器预读对象/边计数时避免构造完整类型数组。
    /// </summary>
    /// <param name="reader">受负载长度和校验约束的快照读取器。</param>
    /// <returns>表示异步跳过操作的任务。</returns>
    private static async Task<int> SkipTypesAsync(PayloadReader reader)
    {
        var count = await reader.ReadInt32Async().ConfigureAwait(false);
        ValidateReadCount(count, MaximumTypeCount, sizeof(int), reader.Remaining);
        for (var index = 0; index < count; index++)
        {
            await reader.SkipStringAsync(nullable: false).ConfigureAwait(false);
            await reader.SkipStringAsync(nullable: true).ConfigureAwait(false);
        }

        return count;
    }

    /// <summary>
    /// 异步读取对象表，并在分配对象数组前以最小记录字节数验证声明数量。
    /// </summary>
    private static async Task<SnapshotIndex.ObjectRow[]> ReadObjectsAsync(PayloadReader reader, TypeIdentity[] types)
    {
        var count = await reader.ReadInt32Async().ConfigureAwait(false);
        ValidateReadCount(count, MaximumObjectCount, 20, reader.Remaining);
        var objects = new SnapshotIndex.ObjectRow[count];
        for (var index = 0; index < objects.Length; index++)
        {
            var address = await reader.ReadUInt64Async().ConfigureAwait(false);
            var typeIndex = await reader.ReadInt32Async().ConfigureAwait(false);
            var size = await reader.ReadInt64Async().ConfigureAwait(false);
            if (address == 0 || (uint)typeIndex >= (uint)types.Length || size < 0)
            {
                throw new InvalidDataException("保留快照对象记录无效。");
            }

            objects[index] = new SnapshotIndex.ObjectRow(address, types[typeIndex], size);
        }

        return objects;
    }

    /// <summary>
    /// 异步读取边表；索引构造阶段忽略零地址，并拒绝不属于对象表的未知非零地址。
    /// </summary>
    private static async Task<EdgeRecord[]> ReadEdgesAsync(PayloadReader reader)
    {
        var count = await reader.ReadInt32Async().ConfigureAwait(false);
        ValidateReadCount(count, MaximumEdgeCount, 16, reader.Remaining);
        var edges = new EdgeRecord[count];
        for (var index = 0; index < edges.Length; index++)
        {
            edges[index] = new EdgeRecord(
                await reader.ReadUInt64Async().ConfigureAwait(false),
                await reader.ReadUInt64Async().ConfigureAwait(false));
        }

        return edges;
    }

    /// <summary>
    /// 异步读取根表，并通过核心模型再次限制函数证据只能属于栈根。
    /// </summary>
    private static async Task<SnapshotIndex.RetentionRootRow[]> ReadRootsAsync(PayloadReader reader)
    {
        var count = await reader.ReadInt32Async().ConfigureAwait(false);
        ValidateReadCount(count, MaximumRootCount, 17, reader.Remaining);
        var roots = new SnapshotIndex.RetentionRootRow[count];
        for (var index = 0; index < roots.Length; index++)
        {
            var address = await reader.ReadUInt64Async().ConfigureAwait(false);
            var kindValue = await reader.ReadByteAsync().ConfigureAwait(false);
            var flagsValue = await reader.ReadInt32Async().ConfigureAwait(false);
            if (!Enum.IsDefined((MemoryRootKind)kindValue))
            {
                throw new InvalidDataException("保留快照根类别无效。");
            }

            var function = await reader.ReadStringAsync(nullable: true).ConfigureAwait(false);
            var module = await reader.ReadStringAsync(nullable: true).ConfigureAwait(false);
            roots[index] = new SnapshotIndex.RetentionRootRow(
                address,
                new MemoryRetentionRoot((MemoryRootKind)kindValue, (MemoryRootFlags)flagsValue, function, module));
        }

        return roots;
    }

    /// <summary>
    /// 在按计数分配数组前校验其不超过协议上限且最小编码长度仍位于剩余负载内。
    /// </summary>
    private static void ValidateReadCount(int value, int maximum, int minimumItemBytes, long remaining)
    {
        if (value < 0 || value > maximum || checked((long)value * minimumItemBytes) > remaining)
        {
            throw new InvalidDataException("保留快照集合计数无效。");
        }
    }

    /// <summary>
    /// 在异步文件流上连续写入一个有长度上限的负载，并在每次写入时更新校验。
    /// </summary>
    private sealed class PayloadWriter
    {
        private readonly FileStream _stream;
        private readonly IncrementalHash _hash;
        private readonly CancellationToken _cancellationToken;
        private readonly byte[] _recordBuffer = new byte[20];
        private readonly byte[] _writeBuffer = new byte[64 * 1024];
        private int _bufferedByteCount;

        /// <summary>
        /// 初始化针对单个输出文件的负载写入器。
        /// </summary>
        public PayloadWriter(FileStream stream, IncrementalHash hash, CancellationToken cancellationToken)
        {
            _stream = stream;
            _hash = hash;
            _cancellationToken = cancellationToken;
        }

        /// <summary>
        /// 已写入的负载字节数。
        /// </summary>
        public long Length { get; private set; }

        /// <summary>
        /// 写入 32 位有符号整数。
        /// </summary>
        public Task WriteInt32Async(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_recordBuffer, value);
            return WriteBytesAsync(_recordBuffer.AsMemory(0, sizeof(int)));
        }

        /// <summary>
        /// 写入 64 位有符号整数。
        /// </summary>
        public Task WriteInt64Async(long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_recordBuffer, value);
            return WriteBytesAsync(_recordBuffer.AsMemory(0, sizeof(long)));
        }

        /// <summary>
        /// 写入 64 位无符号整数。
        /// </summary>
        public Task WriteUInt64Async(ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_recordBuffer, value);
            return WriteBytesAsync(_recordBuffer.AsMemory(0, sizeof(ulong)));
        }

        /// <summary>
        /// 写入单个字节。
        /// </summary>
        public Task WriteByteAsync(byte value)
        {
            _recordBuffer[0] = value;
            return WriteBytesAsync(_recordBuffer.AsMemory(0, 1));
        }

        /// <summary>
        /// 写入一个对象记录，复用会话内缓冲区以避免大图每个对象产生多个小数组分配。
        /// </summary>
        public Task WriteObjectRecordAsync(ulong address, int typeIndex, long sizeBytes)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_recordBuffer, address);
            BinaryPrimitives.WriteInt32LittleEndian(_recordBuffer.AsSpan(sizeof(ulong)), typeIndex);
            BinaryPrimitives.WriteInt64LittleEndian(_recordBuffer.AsSpan(sizeof(ulong) + sizeof(int)), sizeBytes);
            return WriteBytesAsync(_recordBuffer);
        }

        /// <summary>
        /// 写入一个固定宽度引用边记录。
        /// </summary>
        public Task WriteEdgeRecordAsync(ulong sourceAddress, ulong targetAddress)
        {
            var buffer = _recordBuffer.AsSpan(0, sizeof(ulong) * 2);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, sourceAddress);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(sizeof(ulong)), targetAddress);
            return WriteBytesAsync(_recordBuffer.AsMemory(0, sizeof(ulong) * 2));
        }

        /// <summary>
        /// 写入一个根记录及其可选函数证据；函数和模块字符串按既有格式分别编码。
        /// </summary>
        public async Task WriteRootRecordAsync(ulong address, MemoryRetentionRoot root)
        {
            ArgumentNullException.ThrowIfNull(root);
            await WriteUInt64Async(address).ConfigureAwait(false);
            await WriteByteAsync(checked((byte)root.Kind)).ConfigureAwait(false);
            await WriteInt32Async(checked((int)root.Flags)).ConfigureAwait(false);
            await WriteNullableStringAsync(root.FunctionName).ConfigureAwait(false);
            await WriteNullableStringAsync(root.ModuleName).ConfigureAwait(false);
        }

        /// <summary>
        /// 将尚未写入文件的负载缓冲区一次性刷新；调用方在回写文件头前必须建立该顺序边界。
        /// </summary>
        /// <returns>表示缓冲区已写入底层文件流的任务。</returns>
        public async Task FlushAsync()
        {
            if (_bufferedByteCount == 0)
            {
                return;
            }

            await _stream.WriteAsync(_writeBuffer.AsMemory(0, _bufferedByteCount), _cancellationToken).ConfigureAwait(false);
            _bufferedByteCount = 0;
        }

        /// <summary>
        /// 写入不可为空 UTF-8 字符串。
        /// </summary>
        public Task WriteStringAsync(string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            return WriteEncodedStringAsync(value);
        }

        /// <summary>
        /// 写入可为空 UTF-8 字符串；负一长度表示空值。
        /// </summary>
        public Task WriteNullableStringAsync(string? value) =>
            value is null ? WriteInt32Async(-1) : WriteEncodedStringAsync(value);

        /// <summary>
        /// 编码并写入 UTF-8 字符串。
        /// </summary>
        private async Task WriteEncodedStringAsync(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > MaximumStringBytes)
            {
                throw new DiagnosticsException(DiagnosticsErrorCode.SnapshotStorageLimitReached, "保留快照字符串字段超过 1 MiB 上限。");
            }

            await WriteInt32Async(bytes.Length).ConfigureAwait(false);
            await WriteBytesAsync(bytes).ConfigureAwait(false);
        }

        /// <summary>
        /// 写入、计长并增量哈希指定数据。
        /// </summary>
        private async Task WriteBytesAsync(ReadOnlyMemory<byte> data)
        {
            var nextLength = checked(Length + data.Length);
            if (nextLength > MaximumSnapshotBytes)
            {
                throw new DiagnosticsException(DiagnosticsErrorCode.SnapshotStorageLimitReached, "保留分析单快照不能超过 2 GiB。");
            }

            _hash.AppendData(data.Span);
            Length = nextLength;
            if (data.Length <= _writeBuffer.Length - _bufferedByteCount)
            {
                data.CopyTo(_writeBuffer.AsMemory(_bufferedByteCount));
                _bufferedByteCount += data.Length;
                return;
            }

            await FlushAsync().ConfigureAwait(false);
            if (data.Length >= _writeBuffer.Length)
            {
                await _stream.WriteAsync(data, _cancellationToken).ConfigureAwait(false);
                return;
            }

            data.CopyTo(_writeBuffer);
            _bufferedByteCount = data.Length;
        }
    }

    /// <summary>
    /// 在固定声明负载边界中读取、校验并哈希基本字段。
    /// </summary>
    private sealed class PayloadReader
    {
        private readonly FileStream _stream;
        private readonly IncrementalHash _hash;
        private readonly CancellationToken _cancellationToken;
        private readonly byte[] _primitiveBuffer = new byte[sizeof(long)];
        private readonly byte[] _skipBuffer = new byte[64 * 1024];
        private readonly byte[] _readBuffer = new byte[64 * 1024];
        private int _readBufferOffset;
        private int _readBufferCount;

        /// <summary>
        /// 初始化受负载长度限制的读取器。
        /// </summary>
        public PayloadReader(FileStream stream, long payloadLength, IncrementalHash hash, CancellationToken cancellationToken)
        {
            _stream = stream;
            Remaining = payloadLength;
            _hash = hash;
            _cancellationToken = cancellationToken;
        }

        /// <summary>
        /// 尚未消费的负载字节数。
        /// </summary>
        public long Remaining { get; private set; }

        /// <summary>
        /// 读取 32 位有符号整数。
        /// </summary>
        public async Task<int> ReadInt32Async()
        {
            await ReadExactlyIntoAsync(_primitiveBuffer.AsMemory(0, sizeof(int))).ConfigureAwait(false);
            return BinaryPrimitives.ReadInt32LittleEndian(_primitiveBuffer);
        }

        /// <summary>
        /// 读取 64 位有符号整数。
        /// </summary>
        public async Task<long> ReadInt64Async()
        {
            await ReadExactlyIntoAsync(_primitiveBuffer.AsMemory(0, sizeof(long))).ConfigureAwait(false);
            return BinaryPrimitives.ReadInt64LittleEndian(_primitiveBuffer);
        }

        /// <summary>
        /// 读取 64 位无符号整数。
        /// </summary>
        public async Task<ulong> ReadUInt64Async()
        {
            await ReadExactlyIntoAsync(_primitiveBuffer.AsMemory(0, sizeof(ulong))).ConfigureAwait(false);
            return BinaryPrimitives.ReadUInt64LittleEndian(_primitiveBuffer);
        }

        /// <summary>
        /// 读取单个字节。
        /// </summary>
        public async Task<byte> ReadByteAsync()
        {
            await ReadExactlyIntoAsync(_primitiveBuffer.AsMemory(0, 1)).ConfigureAwait(false);
            return _primitiveBuffer[0];
        }

        /// <summary>
        /// 跳过并校验指定数量的负载字节，用于预读取对象/边计数时避免构造整表 DTO。
        /// </summary>
        public async Task SkipAsync(long count)
        {
            if (count < 0 || count > Remaining)
            {
                throw new InvalidDataException("保留快照提前结束。");
            }

            var remaining = count;
            while (remaining > 0)
            {
                var current = (int)Math.Min(_skipBuffer.Length, remaining);
                await ReadExactlyIntoAsync(_skipBuffer.AsMemory(0, current)).ConfigureAwait(false);
                remaining -= current;
            }
        }

        /// <summary>
        /// 跳过带长度前缀的 UTF-8 字符串，同时保留与完整读取路径一致的空值和长度校验。
        /// </summary>
        /// <param name="nullable">是否允许以 -1 长度表示空字符串。</param>
        /// <returns>表示异步跳过操作的任务。</returns>
        public async Task SkipStringAsync(bool nullable)
        {
            var length = await ReadInt32Async().ConfigureAwait(false);
            if (length == -1 && nullable)
            {
                return;
            }

            if (length < 0 || length > MaximumStringBytes || length > Remaining)
            {
                throw new InvalidDataException("保留快照字符串长度无效。");
            }

            await SkipAsync(length).ConfigureAwait(false);
        }

        /// <summary>
        /// 读取带长度前缀的 UTF-8 字符串。
        /// </summary>
        public async Task<string?> ReadStringAsync(bool nullable)
        {
            var length = await ReadInt32Async().ConfigureAwait(false);
            if (length == -1 && nullable)
            {
                return null;
            }

            if (length < 0 || length > MaximumStringBytes || length > Remaining)
            {
                throw new InvalidDataException("保留快照字符串长度无效。");
            }

            var bytes = await ReadExactlyAsync(length).ConfigureAwait(false);
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>
        /// 在不越过声明负载长度的前提下读取指定字节，并将其加入校验。
        /// </summary>
        private async Task<byte[]> ReadExactlyAsync(int count)
        {
            var buffer = new byte[count];
            await ReadExactlyIntoAsync(buffer).ConfigureAwait(false);
            return buffer;
        }

        /// <summary>
        /// 在不越过声明边界的前提下读取到调用方提供的缓冲区，并将原始字节加入快照校验。
        /// </summary>
        private async Task ReadExactlyIntoAsync(Memory<byte> buffer)
        {
            if (buffer.Length > Remaining)
            {
                throw new InvalidDataException("保留快照提前结束。");
            }

            var written = 0;
            while (written < buffer.Length)
            {
                if (_readBufferOffset < _readBufferCount)
                {
                    var count = Math.Min(_readBufferCount - _readBufferOffset, buffer.Length - written);
                    _readBuffer.AsSpan(_readBufferOffset, count).CopyTo(buffer.Span.Slice(written, count));
                    _hash.AppendData(_readBuffer.AsSpan(_readBufferOffset, count));
                    _readBufferOffset += count;
                    Remaining -= count;
                    written += count;
                    continue;
                }

                var remainingDestination = buffer.Length - written;
                if (remainingDestination >= _readBuffer.Length)
                {
                    await _stream.ReadExactlyAsync(buffer.Slice(written, remainingDestination), _cancellationToken).ConfigureAwait(false);
                    _hash.AppendData(buffer.Span.Slice(written, remainingDestination));
                    Remaining -= remainingDestination;
                    written += remainingDestination;
                    continue;
                }

                var requested = (int)Math.Min(_readBuffer.Length, Remaining);
                var read = await _stream.ReadAsync(_readBuffer.AsMemory(0, requested), _cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("保留快照提前结束。");
                }

                _readBufferOffset = 0;
                _readBufferCount = read;
            }
        }
    }
}
