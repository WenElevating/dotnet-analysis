namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示从一个明确 GC 根到目标对象的保留对象链。
/// </summary>
public sealed record MemoryRetentionPath
{
    /// <summary>
    /// 创建保留路径并复制调用方提供的对象序列，避免后续集合修改影响快照结果。
    /// </summary>
    /// <param name="root">路径起点的 GC 根证据。</param>
    /// <param name="objects">按根对象到目标对象顺序排列的对象。</param>
    /// <exception cref="ArgumentNullException">根证据或对象序列为空时引发。</exception>
    /// <exception cref="ArgumentException">对象序列为空时引发。</exception>
    public MemoryRetentionPath(MemoryRetentionRoot root, IReadOnlyList<MemoryObjectInfo> objects)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(objects);
        if (objects.Count == 0)
        {
            throw new ArgumentException("保留路径必须至少包含一个对象。", nameof(objects));
        }

        Root = root;
        Objects = objects.ToArray();
    }

    /// <summary>
    /// 路径起点的 GC 根证据。
    /// </summary>
    public MemoryRetentionRoot Root { get; }

    /// <summary>
    /// 按 GC 根到目标对象顺序排列的对象。
    /// </summary>
    public IReadOnlyList<MemoryObjectInfo> Objects { get; }
}
