namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示执行调用树中的一个节点。
/// </summary>
public sealed record ExecutionCallTreeNode
{
    private readonly ExecutionCallTreeNode[] _children;

    /// <summary>
    /// 创建执行调用树节点。
    /// </summary>
    /// <param name="frame">节点对应的调用栈帧。</param>
    /// <param name="inclusiveSampleCount">包含子调用的非负采样数。</param>
    /// <param name="exclusiveSampleCount">仅该节点的非负采样数。</param>
    /// <param name="children">子调用节点。</param>
    /// <exception cref="ArgumentNullException">调用栈帧或子节点集合为 <see langword="null"/> 时引发。</exception>
    /// <exception cref="ArgumentOutOfRangeException">任一采样数为负数时引发。</exception>
    public ExecutionCallTreeNode(
        ExecutionFrame frame,
        long inclusiveSampleCount,
        long exclusiveSampleCount,
        IReadOnlyList<ExecutionCallTreeNode> children)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(children);
        ValidateSampleCount(inclusiveSampleCount, nameof(inclusiveSampleCount));
        ValidateSampleCount(exclusiveSampleCount, nameof(exclusiveSampleCount));

        Frame = frame;
        InclusiveSampleCount = inclusiveSampleCount;
        ExclusiveSampleCount = exclusiveSampleCount;
        _children = children.ToArray();
        Children = Array.AsReadOnly(_children);
    }

    /// <summary>
    /// 节点对应的调用栈帧。
    /// </summary>
    public ExecutionFrame Frame { get; }

    /// <summary>
    /// 包含子调用的采样数。
    /// </summary>
    public long InclusiveSampleCount { get; }

    /// <summary>
    /// 仅该节点的采样数。
    /// </summary>
    public long ExclusiveSampleCount { get; }

    /// <summary>
    /// 防御性复制后、不可变更的子调用节点视图。
    /// </summary>
    public IReadOnlyList<ExecutionCallTreeNode> Children { get; }

    private static void ValidateSampleCount(long sampleCount, string parameterName)
    {
        if (sampleCount < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, sampleCount, "Sample count cannot be negative.");
        }
    }
}
