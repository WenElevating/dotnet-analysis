namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示从根对象到目标对象的一条引用链。
/// </summary>
public sealed record MemoryReferencePath
{
    /// <summary>
    /// 创建引用路径。
    /// </summary>
    /// <param name="targetObjectAddress">目标对象地址。</param>
    /// <param name="objects">按根到目标顺序排列的对象。</param>
    public MemoryReferencePath(ulong targetObjectAddress, IReadOnlyList<MemoryObjectInfo> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);

        TargetObjectAddress = targetObjectAddress;
        Objects = objects.ToArray();
    }

    /// <summary>
    /// 目标对象地址。
    /// </summary>
    public ulong TargetObjectAddress { get; }

    /// <summary>
    /// 路径上的对象，顺序为根到目标。
    /// </summary>
    public IReadOnlyList<MemoryObjectInfo> Objects { get; }
}
