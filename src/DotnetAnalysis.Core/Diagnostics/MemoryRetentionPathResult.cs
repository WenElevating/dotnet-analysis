namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 汇总一个目标对象的已验证或未知根证据保留路径。
/// </summary>
public sealed record MemoryRetentionPathResult
{
    /// <summary>
    /// 创建目标对象的保留路径结果。
    /// </summary>
    /// <param name="targetObjectAddress">目标对象地址。</param>
    /// <param name="paths">已按稳定优先级排序的保留路径。</param>
    /// <exception cref="ArgumentNullException">路径集合为空时引发。</exception>
    public MemoryRetentionPathResult(ulong targetObjectAddress, IReadOnlyList<MemoryRetentionPath> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        TargetObjectAddress = targetObjectAddress;
        Paths = paths.ToArray();
    }

    /// <summary>
    /// 需要解释其保留原因的目标对象地址。
    /// </summary>
    public ulong TargetObjectAddress { get; }

    /// <summary>
    /// 已按根证据优先级、路径长度和地址稳定排序的路径。
    /// </summary>
    public IReadOnlyList<MemoryRetentionPath> Paths { get; }
}
