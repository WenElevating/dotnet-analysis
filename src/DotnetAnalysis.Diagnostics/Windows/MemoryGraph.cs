using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;
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
    private readonly IReadOnlyList<MemoryObjectInfo>? _objects;
    private readonly IReadOnlyList<HeapGraphNode>? _eventPipeNodes;
    private readonly CancellationToken _cancellationToken;
    private readonly bool _is64Bit = true;

    /// <summary>
    /// 创建供 .gcdump 写入器序列化的托管对象图。
    /// </summary>
    /// <param name="types">图节点引用的类型表。</param>
    /// <param name="labels">每个节点在压缩边数据中的偏移量。</param>
    /// <param name="blob">按 FastSerialization 编码的节点和边数据。</param>
    /// <param name="objects">与图节点索引对齐的实际托管对象。</param>
    /// <param name="cancellationToken">序列化期间用于停止长时间写入的取消令牌。</param>
    public MemoryGraph(
        IReadOnlyList<(string Name, int Size, string? Module)> types,
        int[] labels,
        byte[] blob,
        IReadOnlyList<MemoryObjectInfo> objects,
        CancellationToken cancellationToken)
    {
        _types = types;
        _labels = labels;
        _blob = blob;
        _objects = objects;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// 创建直接引用 EventPipe 紧凑节点行的托管对象图，避免捕获阶段重新投影百万级对象 DTO。
    /// </summary>
    /// <param name="types">图节点引用的类型表。</param>
    /// <param name="labels">每个节点在压缩边数据中的偏移量。</param>
    /// <param name="blob">按 FastSerialization 编码的节点和边数据。</param>
    /// <param name="eventPipeNodes">与图节点索引对齐的紧凑 EventPipe 节点。</param>
    /// <param name="cancellationToken">序列化期间用于停止长时间写入的取消令牌。</param>
    public MemoryGraph(
        IReadOnlyList<(string Name, int Size, string? Module)> types,
        int[] labels,
        byte[] blob,
        IReadOnlyList<HeapGraphNode> eventPipeNodes,
        CancellationToken cancellationToken)
    {
        _types = types;
        _labels = labels;
        _blob = blob;
        _eventPipeNodes = eventPipeNodes;
        _cancellationToken = cancellationToken;
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
        _cancellationToken.ThrowIfCancellationRequested();
        serializer.Write(GetTotalSize());
        serializer.Write(0);
        serializer.Write(_types.Count);
        foreach (var type in _types)
        {
            serializer.Write(type.Name);
            serializer.Write(type.Size);
            serializer.Write(type.Module);
        }

        serializer.Write(_labels.Length);
        for (var index = 0; index < _labels.Length; index++)
        {
            ThrowIfCancellationRequested(index);
            serializer.Write(_labels[index]);
        }

        serializer.Write(_blob.Length);
        for (var index = 0; index < _blob.Length; index++)
        {
            ThrowIfCancellationRequested(index);
            serializer.Write(_blob[index]);
        }

        // The graph contains a synthetic root node at index zero. Keep the
        // address table aligned with the label table by writing a zero address
        // for that root before the real object addresses.
        serializer.Write(_labels.Length);
        serializer.Write(0L);
        if (_eventPipeNodes is not null)
        {
            for (var index = 0; index < _eventPipeNodes.Count; index++)
            {
                ThrowIfCancellationRequested(index);
                serializer.Write(checked((long)_eventPipeNodes[index].Address));
            }
        }
        else
        {
            for (var index = 0; index < _objects!.Count; index++)
            {
                ThrowIfCancellationRequested(index);
                serializer.Write(checked((long)_objects[index].Address));
            }
        }

        serializer.WriteTagged(Is64Bit);
    }

    /// <summary>
    /// 写入器不支持从流还原内存图。
    /// </summary>
    void IFastSerializable.FromStream(Deserializer deserializer) =>
        throw new NotSupportedException("The diagnostics writer is write-only.");

    private long GetTotalSize()
    {
        long totalSize = 0;
        if (_eventPipeNodes is not null)
        {
            for (var index = 0; index < _eventPipeNodes.Count; index++)
            {
                ThrowIfCancellationRequested(index);
                totalSize = checked(totalSize + _eventPipeNodes[index].SizeBytes);
            }

            return totalSize;
        }

        for (var index = 0; index < _objects!.Count; index++)
        {
            ThrowIfCancellationRequested(index);
            totalSize = checked(totalSize + _objects[index].SizeBytes);
        }

        return totalSize;
    }

    private void ThrowIfCancellationRequested(int index)
    {
        if ((index & 0x3fff) == 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
