using FastSerialization;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 把统一堆模型编码为标准 FastSerialization .gcdump 文件。
/// </summary>
internal static class GCDumpFastSerializationWriter
{
    /// <summary>
    /// 将内存图包装成 FastSerialization .gcdump 文件。
    /// </summary>
    /// <param name="outputPath">输出文件路径。</param>
    /// <param name="heap">已解析的托管堆节点、边和根集合。</param>
    /// <param name="target">产生该堆快照的目标进程。</param>
    /// <param name="capturedAtUtc">快照捕获完成时间。</param>
    /// <param name="cancellationToken">构建过程中用于停止工作的取消令牌。</param>
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
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// 将 EventPipe 消费期间已构建的紧凑堆图直接编码为 .gcdump，避免重新创建全量对象和边集合。
    /// </summary>
    /// <param name="outputPath">输出文件路径。</param>
    /// <param name="heapBuilder">已完成 EventPipe 消费的堆图构建器。</param>
    /// <param name="target">产生该堆快照的目标进程。</param>
    /// <param name="capturedAtUtc">快照捕获完成时间。</param>
    /// <param name="cancellationToken">构建过程中用于停止工作的取消令牌。</param>
    public static void Write(
        string outputPath,
        EventPipeHeapBuilder heapBuilder,
        TargetProcess target,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(heapBuilder);
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        var graph = BuildGraph(heapBuilder, cancellationToken);
        var dump = new GCHeapDump(graph, target, capturedAtUtc);
        var serializer = new Serializer(outputPath, dump, FileShare.Read);
        serializer.Close();
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// 把解析结果转换为带合成根节点的 FastSerialization 图结构。
    /// </summary>
    private static Graphs.MemoryGraph BuildGraph(
        GCDumpSnapshotReader.HeapData heap,
        CancellationToken cancellationToken)
    {
        var objects = heap.Objects.ToArray();
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
            objects,
            cancellationToken);
    }

    /// <summary>
    /// 直接将 EventPipe 的节点行和顺序边槽位编码为 FastSerialization 图。
    /// </summary>
    private static Graphs.MemoryGraph BuildGraph(
        EventPipeHeapBuilder heapBuilder,
        CancellationToken cancellationToken)
    {
        var nodes = heapBuilder.Nodes;
        var edgeTargets = heapBuilder.EdgeTargets;
        var addressToIndex = new Dictionary<ulong, int>(nodes.Count);
        for (var index = 0; index < nodes.Count; index++)
        {
            addressToIndex.TryAdd(nodes[index].Address, index + 1);
        }

        var typeEntries = new List<(string Name, int Size, string? Module)>
        {
            ("UNDEFINED", 0, null),
            ("[.NET Roots]", 0, null)
        };
        var typeIndexes = new Dictionary<TypeIdentity, int>();
        foreach (var node in nodes)
        {
            if (typeIndexes.TryGetValue(node.Type, out _))
            {
                continue;
            }

            var index = typeEntries.Count;
            typeIndexes.Add(node.Type, index);
            typeEntries.Add((
                node.Type.TypeName,
                checked((int)Math.Min(node.SizeBytes, int.MaxValue)),
                node.Type.AssemblyName));
        }

        var estimatedBlobBytes = checked(nodes.Count * 4L + edgeTargets.Count * 2L);
        using var blob = new MemoryStream(checked((int)Math.Min(estimatedBlobBytes, 64L * 1024 * 1024)));
        using var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, leaveOpen: true);
        var labels = new int[nodes.Count + 1];
        Array.Fill(labels, -1);
        WriteNode(writer, labels, 0, 1, 0, GetRootChildren(heapBuilder.Roots, addressToIndex));

        var edgeOffset = 0;
        for (var objectOffset = 0; objectOffset < nodes.Count; objectOffset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = nodes[objectOffset];
            var availableEdges = edgeTargets.Count - edgeOffset;
            var edgeCount = availableEdges > 0
                ? checked((int)Math.Min(Math.Max(node.EdgeCount, 0), availableEdges))
                : 0;
            var typeIndex = typeIndexes[node.Type];
            var canonicalSize = typeEntries[typeIndex].Size;
            var sizeOverride = node.SizeBytes != canonicalSize;
            WriteNode(
                writer,
                labels,
                objectOffset + 1,
                typeIndex,
                sizeOverride ? checked((int)Math.Min(node.SizeBytes, int.MaxValue)) : null,
                edgeTargets,
                edgeOffset,
                edgeCount,
                addressToIndex);
            edgeOffset += edgeCount;
        }

        writer.Flush();
        return new Graphs.MemoryGraph(typeEntries, labels, blob.ToArray(), nodes, cancellationToken);
    }

    /// <summary>
    /// 将堆根地址映射为图节点索引并去除重复项。
    /// </summary>
    private static int[] GetRootChildren(
        GCDumpSnapshotReader.HeapData heap,
        Dictionary<ulong, int> addressToIndex) =>
        heap.Roots
            .Where(addressToIndex.ContainsKey)
            .Select(address => addressToIndex[address])
            .Distinct()
            .ToArray();

    /// <summary>
    /// 将 EventPipe 根地址映射为图节点索引并去除重复项。
    /// </summary>
    private static int[] GetRootChildren(
        IReadOnlyList<ulong> roots,
        Dictionary<ulong, int> addressToIndex) =>
        roots
            .Where(addressToIndex.ContainsKey)
            .Select(address => addressToIndex[address])
            .Distinct()
            .ToArray();

    /// <summary>
    /// 写入一个节点的类型、大小覆盖值和子节点相对索引。
    /// </summary>
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

    /// <summary>
    /// 从连续 EventPipe 边槽位直接写入节点，避免为每个对象创建中间子节点数组。
    /// </summary>
    private static void WriteNode(
        BinaryWriter writer,
        int[] labels,
        int nodeIndex,
        int typeIndex,
        int? sizeOverride,
        IReadOnlyList<ulong> edgeTargets,
        int edgeOffset,
        int edgeCount,
        Dictionary<ulong, int> addressToIndex)
    {
        labels[nodeIndex] = checked((int)writer.BaseStream.Position);
        WriteCompressedInt(writer, checked((typeIndex << 1) | (sizeOverride.HasValue ? 1 : 0)));
        if (sizeOverride.HasValue)
        {
            WriteCompressedInt(writer, sizeOverride.Value);
        }

        var childCount = 0;
        for (var index = 0; index < edgeCount; index++)
        {
            if (addressToIndex.ContainsKey(edgeTargets[edgeOffset + index]))
            {
                childCount++;
            }
        }

        WriteCompressedInt(writer, childCount);
        for (var index = 0; index < edgeCount; index++)
        {
            if (addressToIndex.TryGetValue(edgeTargets[edgeOffset + index], out var child))
            {
                WriteCompressedInt(writer, checked(child - nodeIndex));
            }
        }
    }

    /// <summary>
    /// 按 FastSerialization 约定写入压缩有符号整数。
    /// </summary>
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

    /// <summary>
    /// 把诊断堆图适配为 .gcdump 所需的 FastSerialization 根对象。
    /// </summary>
    private sealed class GCHeapDump : IFastSerializable, IFastSerializableVersion
    {
        private readonly Graphs.MemoryGraph _graph;
        private readonly TargetProcess _target;
        private readonly DateTimeOffset _capturedAtUtc;

        /// <summary>
        /// 创建包含堆图、目标进程和捕获时间的序列化对象。
        /// </summary>
        /// <param name="graph">待写入的堆图。</param>
        /// <param name="target">目标进程信息。</param>
        /// <param name="capturedAtUtc">快照捕获完成时间。</param>
        public GCHeapDump(Graphs.MemoryGraph graph, TargetProcess target, DateTimeOffset capturedAtUtc)
        {
            _graph = graph;
            _target = target;
            _capturedAtUtc = capturedAtUtc;
        }

        /// <summary>
        /// 报告当前 .gcdump 根对象的序列化版本。
        /// </summary>
        int IFastSerializableVersion.Version => 10;

        /// <summary>
        /// 报告可读取该对象的最低格式版本。
        /// </summary>
        int IFastSerializableVersion.MinimumVersionCanRead => 4;

        /// <summary>
        /// 报告读取该对象所需的最低读取器版本。
        /// </summary>
        int IFastSerializableVersion.MinimumReaderVersion => 8;

        /// <summary>
        /// 按 GCHeapDump 格式写入堆图和进程元数据。
        /// </summary>
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

        /// <summary>
        /// 写入器不支持从流反序列化。
        /// </summary>
        void IFastSerializable.FromStream(Deserializer deserializer) =>
            throw new NotSupportedException("The diagnostics writer is write-only.");
    }
}
