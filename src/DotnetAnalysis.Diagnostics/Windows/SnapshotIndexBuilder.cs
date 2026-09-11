using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 在读取 .gcdump 时直接累积紧凑索引输入，避免创建全量对象 DTO 和二次复制。
/// </summary>
internal sealed class SnapshotIndexBuilder
{
    private readonly List<SnapshotIndex.ObjectRow> _objects = [];
    private readonly Dictionary<ulong, IReadOnlyList<ulong>> _edges = [];
    private readonly List<ulong> _roots = [];
    private readonly List<SnapshotIndex.RetentionRootRow> _retentionRoots = [];

    /// <summary>
    /// 添加一个值类型对象行。
    /// </summary>
    public void AddObject(ulong address, TypeIdentity type, long sizeBytes) =>
        _objects.Add(new SnapshotIndex.ObjectRow(address, type, sizeBytes));

    /// <summary>
    /// 添加一个仅包含非空地址的正向引用边集合。
    /// </summary>
    public void AddEdges(ulong source, IReadOnlyList<ulong> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var nonEmpty = targets.Where(address => address != 0).Distinct().ToArray();
        if (nonEmpty.Length != 0)
        {
            _edges[source] = nonEmpty;
        }
    }

    /// <summary>
    /// 添加一个真实 GC Root 指向的对象地址。
    /// </summary>
    public void AddRoot(ulong address)
    {
        if (address != 0)
        {
            _roots.Add(address);
        }
    }

    /// <summary>
    /// 添加带 CLR 标志或函数证据的 GC Root；弱根由最终索引统一排除出保留分析。
    /// </summary>
    /// <param name="address">根直接指向的对象地址。</param>
    /// <param name="root">不得伪造的根类别、标志和可选函数证据。</param>
    public void AddRoot(ulong address, MemoryRetentionRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (address != 0)
        {
            _retentionRoots.Add(new SnapshotIndex.RetentionRootRow(address, root));
        }
    }

    /// <summary>
    /// 冻结当前输入为常驻紧凑索引。
    /// </summary>
    public SnapshotIndex Build() => new(_objects, _edges, _roots, retentionRoots: _retentionRoots);
}
