using FastSerialization;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

internal static class GCDumpFastSerializationWriter
{
    public static void Write(
        string outputPath,
        GCDumpSnapshotReader.HeapData heap,
        TargetProcess target,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(heap);
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        var graph = BuildGraph(heap, cancellationToken);
        var dump = new GCHeapDump(graph, target, capturedAtUtc);
        var serializer = new Serializer(outputPath, dump, FileShare.Read);
        serializer.Close();
    }

    private static Graphs.MemoryGraph BuildGraph(
        GCDumpSnapshotReader.HeapData heap,
        CancellationToken cancellationToken)
    {
        var objects = heap.Objects
            .GroupBy(candidate => candidate.Address)
            .Select(group => group.First())
            .ToArray();
        var addressToIndex = objects
            .Select((candidate, index) => (candidate.Address, Index: index + 1))
            .ToDictionary(item => item.Address, item => item.Index);

        var typeEntries = new List<(string Name, int Size, string? Module)>
        {
            ("UNDEFINED", 0, null),
            ("[.NET Roots]", 0, null)
        };
        var typeIndexes = new Dictionary<TypeIdentity, int>();
        foreach (var candidate in objects)
        {
            if (!typeIndexes.ContainsKey(candidate.Type))
            {
                var index = typeEntries.Count;
                typeIndexes[candidate.Type] = index;
                typeEntries.Add((candidate.Type.TypeName, checked((int)Math.Min(candidate.SizeBytes, int.MaxValue)), candidate.Type.AssemblyName));
            }
        }

        using var blob = new MemoryStream();
        using var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, leaveOpen: true);
        var labels = new int[objects.Length + 1];
        Array.Fill(labels, -1);
        WriteNode(writer, labels, 0, 1, 0, GetRootChildren(heap, addressToIndex));

        for (var objectOffset = 0; objectOffset < objects.Length; objectOffset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nodeIndex = objectOffset + 1;
            var candidate = objects[objectOffset];
            var typeIndex = typeIndexes[candidate.Type];
            var canonicalSize = typeEntries[typeIndex].Size;
            var sizeOverride = candidate.SizeBytes != canonicalSize;
            var children = heap.Edges.TryGetValue(candidate.Address, out var targets)
                ? targets
                    .Where(addressToIndex.ContainsKey)
                    .Select(address => addressToIndex[address])
                    .Distinct()
                    .ToArray()
                : Array.Empty<int>();
            WriteNode(writer, labels, nodeIndex, typeIndex, sizeOverride ? checked((int)Math.Min(candidate.SizeBytes, int.MaxValue)) : null, children);
        }

        writer.Flush();
        return new Graphs.MemoryGraph(
            typeEntries,
            labels,
            blob.ToArray(),
            objects);
    }

    private static int[] GetRootChildren(
        GCDumpSnapshotReader.HeapData heap,
        Dictionary<ulong, int> addressToIndex) =>
        heap.Roots
            .Where(addressToIndex.ContainsKey)
            .Select(address => addressToIndex[address])
            .Distinct()
            .ToArray();

    private static void WriteNode(
        BinaryWriter writer,
        int[] labels,
        int nodeIndex,
        int typeIndex,
        int? sizeOverride,
        int[] children)
    {
        labels[nodeIndex] = checked((int)writer.BaseStream.Position);
        WriteCompressedInt(writer, checked((typeIndex << 1) | (sizeOverride.HasValue ? 1 : 0)));
        if (sizeOverride.HasValue)
        {
            WriteCompressedInt(writer, sizeOverride.Value);
        }

        WriteCompressedInt(writer, children.Length);
        foreach (var child in children)
        {
            WriteCompressedInt(writer, checked(child - nodeIndex));
        }
    }

    private static void WriteCompressedInt(BinaryWriter writer, int value)
    {
        if (value << 25 >> 25 != value)
        {
            if (value << 18 >> 18 != value)
            {
                if (value << 11 >> 11 != value)
                {
                    if (value << 4 >> 4 != value)
                    {
                        writer.Write((byte)((value >> 28) | 0x80));
                    }

                    writer.Write((byte)((value >> 21) | 0x80));
                }

                writer.Write((byte)((value >> 14) | 0x80));
            }

            writer.Write((byte)((value >> 7) | 0x80));
        }

        writer.Write((byte)(value & 0x7F));
    }

    private sealed class GCHeapDump : IFastSerializable, IFastSerializableVersion
    {
        private readonly Graphs.MemoryGraph _graph;
        private readonly TargetProcess _target;
        private readonly DateTimeOffset _capturedAtUtc;

        public GCHeapDump(Graphs.MemoryGraph graph, TargetProcess target, DateTimeOffset capturedAtUtc)
        {
            _graph = graph;
            _target = target;
            _capturedAtUtc = capturedAtUtc;
        }

        int IFastSerializableVersion.Version => 10;

        int IFastSerializableVersion.MinimumVersionCanRead => 4;

        int IFastSerializableVersion.MinimumReaderVersion => 8;

        void IFastSerializable.ToStream(Serializer serializer)
        {
            serializer.Write(_graph);
            serializer.Write(_graph.Is64Bit);
            serializer.Write(1f);
            serializer.Write(1f);
            serializer.Write((IFastSerializable?)null);
            serializer.Write((IFastSerializable?)null);
            serializer.Write((string?)null);
            serializer.Write(_capturedAtUtc.UtcDateTime.Ticks);
            serializer.Write(Environment.MachineName);
            serializer.Write(_target.ProcessName);
            serializer.Write(_target.ProcessId);
            serializer.Write(0L);
            serializer.Write(0L);
            serializer.Write(0);
            serializer.WriteTagged((IFastSerializable?)null);
            serializer.WriteTagged("DotnetAnalysis");
        }

        void IFastSerializable.FromStream(Deserializer deserializer) =>
            throw new NotSupportedException("The diagnostics writer is write-only.");
    }
}
