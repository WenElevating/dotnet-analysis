using DotnetAnalysis.Core.Diagnostics;
using FastSerialization;

namespace Graphs;

/// <summary>
/// 保存 FastSerialization 所需的类型表、节点索引和边数据。
/// </summary>
internal sealed class MemoryGraph : IFastSerializable, IFastSerializableVersion
{
    private readonly IReadOnlyList<(string Name, int Size, string? Module)> _types;
    private readonly int[] _labels;
    private readonly byte[] _blob;
    private readonly IReadOnlyList<MemoryObjectInfo> _objects;
    private readonly bool _is64Bit = true;

    /// <summary>
    /// 创建供 .gcdump 写入器序列化的托管对象图。
    /// </summary>
    /// <param name="types">图节点引用的类型表。</param>
    /// <param name="labels">每个节点在压缩边数据中的偏移量。</param>
    /// <param name="blob">按 FastSerialization 编码的节点和边数据。</param>
    /// <param name="objects">与图节点索引对齐的实际托管对象。</param>
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

    /// <summary>
    /// 图是否使用 64 位对象地址。
    /// </summary>
    public bool Is64Bit => _is64Bit;

    /// <summary>
    /// 报告图对象的序列化版本。
    /// </summary>
    int IFastSerializableVersion.Version => 1;

    /// <summary>
    /// 报告可读取图对象的最低格式版本。
    /// </summary>
    int IFastSerializableVersion.MinimumVersionCanRead => 0;

    /// <summary>
    /// 报告读取图对象所需的最低读取器版本。
    /// </summary>
    int IFastSerializableVersion.MinimumReaderVersion => 0;

    /// <summary>
    /// 把类型表、节点数据、地址表和位数信息写入序列化流。
    /// </summary>
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

    /// <summary>
    /// 写入器不支持从流还原内存图。
    /// </summary>
    void IFastSerializable.FromStream(Deserializer deserializer) =>
        throw new NotSupportedException("The diagnostics writer is write-only.");
}
