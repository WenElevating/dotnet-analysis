using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using System.Collections.ObjectModel;
using System.Text;

namespace DotnetAnalysis.Diagnostics.Windows;

public sealed class GCDumpSnapshotReader : IMemorySnapshotReader
{
    public bool CanRead(string filePath) => string.Equals(Path.GetExtension(filePath), ".gcdump", StringComparison.OrdinalIgnoreCase);

    public Task<IReadOnlyList<MemoryTypeSummary>> ReadTypeSummariesAsync(string filePath, CancellationToken cancellationToken)
    {
        Validate(filePath, cancellationToken);
        return Task.Run(() => ReadHeap(filePath, cancellationToken).TypeSummaries, cancellationToken);
    }

    public Task<IReadOnlyList<MemoryObjectInfo>> ReadObjectsAsync(string filePath, TypeIdentity type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(type);
        Validate(filePath, cancellationToken);
        return Task.Run<IReadOnlyList<MemoryObjectInfo>>(
            () => ReadHeap(filePath, cancellationToken).Objects
                .Where(candidate => candidate.Type == type)
                .ToArray(),
            cancellationToken);
    }

    public Task<MemoryReferencePath?> ReadReferencePathAsync(string filePath, ulong objectAddress, CancellationToken cancellationToken)
    {
        Validate(filePath, cancellationToken);
        return Task.Run(
            () =>
            {
                var heap = ReadHeap(filePath, cancellationToken);
                return BuildReferencePath(heap, objectAddress);
            },
            cancellationToken);
    }

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

    private static HeapData ReadHeap(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryReadFastSerializationGcdump(filePath, cancellationToken, out var serializedHeap))
        {
            return serializedHeap;
        }

        var typeNames = new Dictionary<ulong, TypeIdentity>();
        var nodes = new List<NodeData>();
        var edgeTargets = new List<ulong>();
        var roots = new List<ulong>();
        var objects = new List<MemoryObjectInfo>();
        var aggregate = new Dictionary<TypeIdentity, (long Count, long Size)>();
        var sawHeapEvent = false;

        try
        {
            using var source = new EventPipeEventSource(filePath);
            source.Clr.TypeBulkType += data =>
            {
                for (var index = 0; index < data.Count; index++)
                {
                    var value = data.Values(index);
                    var name = string.IsNullOrWhiteSpace(value.TypeName)
                        ? $"Type(0x{value.TypeNameID:x})"
                        : value.TypeName;
                    typeNames[value.TypeID] = new TypeIdentity(name, null);
                }
            };
            source.Clr.GCBulkNode += data =>
            {
                for (var index = 0; index < data.Count; index++)
                {
                    var value = data.Values(index);
                    if (value.Size > long.MaxValue)
                    {
                        continue;
                    }

                    sawHeapEvent = true;
                    var type = typeNames.TryGetValue(value.TypeID, out var knownType)
                        ? knownType
                        : new TypeIdentity($"Type(0x{value.TypeID:x})", null);
                    var objectInfo = new MemoryObjectInfo(value.Address, type, (long)value.Size);
                    nodes.Add(new NodeData(objectInfo, value.EdgeCount));
                    objects.Add(objectInfo);
                    aggregate.TryGetValue(type, out var current);
                    aggregate[type] = (current.Count + 1, checked(current.Size + objectInfo.SizeBytes));
                }
            };
            source.Clr.GCBulkEdge += data =>
            {
                for (var index = 0; index < data.Count; index++)
                {
                    edgeTargets.Add(data.Values(index).Target);
                }
            };
            source.Clr.GCBulkRootEdge += data =>
            {
                for (var index = 0; index < data.Count; index++)
                {
                    var address = data.Values(index).RootedNodeAddress;
                    if (address != 0)
                    {
                        roots.Add(address);
                    }
                }
            };
            source.Process();
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

        if (!sawHeapEvent)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.CaptureFailed,
                "The gcdump did not contain heap object events.");
        }

        var summaries = aggregate
            .Select(entry => new MemoryTypeSummary(entry.Key, entry.Value.Count, entry.Value.Size))
            .OrderByDescending(summary => summary.TotalSizeBytes)
            .ThenBy(summary => summary.Type.TypeName, StringComparer.Ordinal)
            .ToArray();
        return new HeapData(
            new ReadOnlyCollection<MemoryTypeSummary>(summaries),
            new ReadOnlyCollection<MemoryObjectInfo>(objects),
            BuildEdges(nodes, edgeTargets),
            roots.Distinct().ToArray());
    }

    internal static HeapData ReadHeapForSerialization(
        string filePath,
        CancellationToken cancellationToken) =>
        ReadHeap(filePath, cancellationToken);

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

    private enum FastTag : byte
    {
        BeginObject = 4,
        NullReference = 1,
        EndObject = 6,
        Byte = 8
    }

    private sealed class FastSerializationReader : IDisposable
    {
        private readonly FileStream _stream;
        private readonly BinaryReader _binaryReader;

        public FastSerializationReader(string filePath)
        {
            _stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _binaryReader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
        }

        public long Remaining => _stream.Length - _stream.Position;

        public int ReadInt32() => _binaryReader.ReadInt32();

        public long ReadInt64() => _binaryReader.ReadInt64();

        public byte ReadByte() => _binaryReader.ReadByte();

        public byte[] ReadBytes(int count)
        {
            var bytes = _binaryReader.ReadBytes(count);
            if (bytes.Length != count)
            {
                throw new EndOfStreamException();
            }

            return bytes;
        }

        public string ReadUtf8(int byteCount) => Encoding.UTF8.GetString(ReadBytes(byteCount));

        public string? ReadString()
        {
            var byteCount = ReadInt32();
            return byteCount < 0 ? null : ReadUtf8(byteCount);
        }

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

        public void ExpectTag(FastTag expected)
        {
            var actual = (FastTag)ReadByte();
            if (actual != expected)
            {
                throw new InvalidDataException($"Expected tag {expected}, got {actual}.");
            }
        }

        public void Dispose()
        {
            _binaryReader.Dispose();
            _stream.Dispose();
        }
    }

    private ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> _buffer;
        private int _position;

        public SpanReader(ReadOnlySpan<byte> buffer)
        {
            _buffer = buffer;
            _position = 0;
        }

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

        private byte ReadByte()
        {
            if ((uint)_position >= (uint)_buffer.Length)
            {
                throw new EndOfStreamException();
            }

            return _buffer[_position++];
        }

        private byte PeekByte()
        {
            if ((uint)_position >= (uint)_buffer.Length)
            {
                throw new EndOfStreamException();
            }

            return _buffer[_position];
        }
    }

    internal sealed record HeapData(
        IReadOnlyList<MemoryTypeSummary> TypeSummaries,
        IReadOnlyList<MemoryObjectInfo> Objects,
        IReadOnlyDictionary<ulong, IReadOnlyList<ulong>> Edges,
        IReadOnlyList<ulong> Roots);

    private sealed record NodeData(MemoryObjectInfo Object, long EdgeCount);

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
