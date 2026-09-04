using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using System.Collections.ObjectModel;
using System.Text;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 读取 FastSerialization 或 EventPipe .gcdump 并提供统一堆查询。
/// </summary>
public sealed class GCDumpSnapshotReader : IMemorySnapshotReader
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
                or ArgumentException)
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

    /// <summary>
    /// 读取供同一快照查询复用的紧凑索引。
    /// </summary>
    internal static Task<SnapshotIndex> ReadIndexAsync(string filePath) =>
        Task.Run(() => ReadIndex(filePath, CancellationToken.None), CancellationToken.None);

    /// <summary>
    /// 直接从快照流生成紧凑索引；不会先构造完整 <see cref="HeapData"/> 对象图。
    /// </summary>
    private static SnapshotIndex ReadIndex(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryReadFastSerializationIndex(filePath, cancellationToken, out var serializedIndex))
        {
            return serializedIndex;
        }

        try
        {
            using var source = new EventPipeEventSource(filePath);
            var builder = new EventPipeHeapBuilder();
            builder.Attach(source);
            source.Process();
            return builder.BuildIndex();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or IOException
                or EndOfStreamException
                or ArgumentException)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.CaptureFailed,
                "The gcdump could not be parsed.",
                exception);
        }
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
    {
        var index = await ReadIndexAsync(filePath).WaitAsync(cancellationToken).ConfigureAwait(false);
        return index.TypeSummaries;
    }

    private static async Task<IReadOnlyList<MemoryObjectInfo>> ReadObjectsCoreAsync(
        string filePath,
        TypeIdentity type,
        CancellationToken cancellationToken)
    {
        var index = await ReadIndexAsync(filePath).WaitAsync(cancellationToken).ConfigureAwait(false);
        return index.GetObjects(type);
    }

    private static async Task<MemoryReferencePath?> ReadReferencePathCoreAsync(
        string filePath,
        ulong objectAddress,
        CancellationToken cancellationToken)
    {
        var index = await ReadIndexAsync(filePath).WaitAsync(cancellationToken).ConfigureAwait(false);
        return index.GetReferencePath(objectAddress);
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
            var first = ReadByte();
            var result = first << 25 >> 25;
            if ((first & 0x80) == 0)
            {
                return result;
            }

            result = (result << 7) + (ReadByte() & 0x7F);
            if ((PeekByte() & 0x80) == 0)
            {
                return result;
            }

            result = (result << 7) + (ReadByte() & 0x7F);
            if ((PeekByte() & 0x80) == 0)
            {
                return result;
            }

            result = (result << 7) + (ReadByte() & 0x7F);
            if ((PeekByte() & 0x80) == 0)
            {
                return result;
            }

            result = (result << 7) + ReadByte();
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

        /// <summary>
        /// 查看下一个缓冲区字节但不推进位置。
        /// </summary>
        private byte PeekByte()
        {
            if ((uint)_position >= (uint)_buffer.Length)
            {
                throw new EndOfStreamException();
            }

            return _buffer[_position];
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
