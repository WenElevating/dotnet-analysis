using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 定义可提供紧凑对象索引的内部快照读取器边界，避免分析服务依赖某个具体文件格式。
/// </summary>
internal interface IIndexedMemorySnapshotReader : IMemorySnapshotReader
{
    /// <summary>
    /// 异步读取供同一快照后续查询共享的紧凑索引。
    /// </summary>
    /// <param name="filePath">待解析快照的本地绝对路径。</param>
    /// <returns>已验证且可查询的常驻索引。</returns>
    Task<SnapshotIndex> ReadIndexAsync(string filePath);
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
    private const int MaximumObjectCount = 100_000_000;
    private const int MaximumEdgeCount = 500_000_000;
    private const int MaximumRootCount = 100_000_000;

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
    /// 异步读取边表；地址是否属于对象表由索引构造阶段统一过滤。
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
            var buffer = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
            return WriteBytesAsync(buffer);
        }

        /// <summary>
        /// 写入 64 位有符号整数。
        /// </summary>
        public Task WriteInt64Async(long value)
        {
            var buffer = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
            return WriteBytesAsync(buffer);
        }

        /// <summary>
        /// 写入 64 位无符号整数。
        /// </summary>
        public Task WriteUInt64Async(ulong value)
        {
            var buffer = new byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
            return WriteBytesAsync(buffer);
        }

        /// <summary>
        /// 写入单个字节。
        /// </summary>
        public Task WriteByteAsync(byte value) => WriteBytesAsync(new byte[] { value });

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

            await _stream.WriteAsync(data, _cancellationToken).ConfigureAwait(false);
            _hash.AppendData(data.Span);
            Length = nextLength;
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
            var buffer = await ReadExactlyAsync(sizeof(int)).ConfigureAwait(false);
            return BinaryPrimitives.ReadInt32LittleEndian(buffer);
        }

        /// <summary>
        /// 读取 64 位有符号整数。
        /// </summary>
        public async Task<long> ReadInt64Async()
        {
            var buffer = await ReadExactlyAsync(sizeof(long)).ConfigureAwait(false);
            return BinaryPrimitives.ReadInt64LittleEndian(buffer);
        }

        /// <summary>
        /// 读取 64 位无符号整数。
        /// </summary>
        public async Task<ulong> ReadUInt64Async()
        {
            var buffer = await ReadExactlyAsync(sizeof(ulong)).ConfigureAwait(false);
            return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        }

        /// <summary>
        /// 读取单个字节。
        /// </summary>
        public async Task<byte> ReadByteAsync() => (await ReadExactlyAsync(1).ConfigureAwait(false))[0];

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
            if (count < 0 || count > Remaining)
            {
                throw new InvalidDataException("保留快照提前结束。");
            }

            var buffer = new byte[count];
            await _stream.ReadExactlyAsync(buffer, _cancellationToken).ConfigureAwait(false);
            _hash.AppendData(buffer);
            Remaining -= count;
            return buffer;
        }
    }
}
