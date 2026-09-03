using DotnetAnalysis.Core.Diagnostics;
using FastSerialization;

namespace Graphs;

internal sealed class MemoryGraph : IFastSerializable, IFastSerializableVersion
{
    private readonly IReadOnlyList<(string Name, int Size, string? Module)> _types;
    private readonly int[] _labels;
    private readonly byte[] _blob;
    private readonly IReadOnlyList<MemoryObjectInfo> _objects;
    private readonly bool _is64Bit = true;

    public MemoryGraph(
        IReadOnlyList<(string Name, int Size, string? Module)> types,
        int[] labels,
        byte[] blob,
        IReadOnlyList<MemoryObjectInfo> objects)
    {
        _types = types;
        _labels = labels;
        _blob = blob;
        _objects = objects;
    }

    public bool Is64Bit => _is64Bit;

    int IFastSerializableVersion.Version => 1;

    int IFastSerializableVersion.MinimumVersionCanRead => 0;

    int IFastSerializableVersion.MinimumReaderVersion => 0;

    void IFastSerializable.ToStream(Serializer serializer)
    {
        serializer.Write(_objects.Sum(candidate => candidate.SizeBytes));
        serializer.Write(0);
        serializer.Write(_types.Count);
        foreach (var type in _types)
        {
            serializer.Write(type.Name);
            serializer.Write(type.Size);
            serializer.Write(type.Module);
        }

        serializer.Write(_labels.Length);
        foreach (var label in _labels)
        {
            serializer.Write(label);
        }

        serializer.Write(_blob.Length);
        foreach (var value in _blob)
        {
            serializer.Write(value);
        }

        // The graph contains a synthetic root node at index zero. Keep the
        // address table aligned with the label table by writing a zero address
        // for that root before the real object addresses.
        serializer.Write(_labels.Length);
        serializer.Write(0L);
        foreach (var candidate in _objects)
        {
            serializer.Write(checked((long)candidate.Address));
        }

        serializer.WriteTagged(Is64Bit);
    }

    void IFastSerializable.FromStream(Deserializer deserializer) =>
        throw new NotSupportedException("The diagnostics writer is write-only.");
}
