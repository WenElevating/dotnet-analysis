using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 将固定读取边界内的执行采样流式聚合为调用树和热点。
/// </summary>
internal sealed class ExecutionProfileBuilder
{
    private readonly ExecutionCaptureStore _store;

    /// <summary>
    /// 创建执行采样聚合器。
    /// </summary>
    /// <param name="store">提供固定边界样本和调用栈帧的会话存储。</param>
    public ExecutionProfileBuilder(ExecutionCaptureStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// 在固定读取边界内按时间范围构建执行采样分析结果。
    /// </summary>
    /// <param name="range">要聚合的半开 UTC 时间范围。</param>
    /// <param name="boundary">调用方已固定的读取边界。</param>
    /// <param name="lostEventCount">同一查询边界内累计的丢失事件数。</param>
    /// <param name="cancellationToken">取消流式读取和聚合的令牌。</param>
    /// <returns>按稳定顺序排列热点且包含调用树的执行分析结果。</returns>
    public async Task<ExecutionProfile> BuildAsync(
        ExecutionTimeRange range,
        ExecutionCaptureReadBoundary boundary,
        long lostEventCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentOutOfRangeException.ThrowIfNegative(lostEventCount);
        cancellationToken.ThrowIfCancellationRequested();

        var framesByStackId = new Dictionary<int, IReadOnlyList<ExecutionFrameDescriptor>>();
        var coreFramesByDescriptor = new Dictionary<ExecutionFrameDescriptor, ExecutionFrame>();
        var hotspotsByDescriptor = new Dictionary<ExecutionFrameDescriptor, MutableHotspot>();
        var callTreeRootsByDescriptor = new Dictionary<ExecutionFrameDescriptor, MutableCallTreeNode>();
        var nextFirstSeenOrder = 0L;
        long receivedSampleCount = 0;

        await foreach (var sample in _store.ReadAsync(range, boundary, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            receivedSampleCount = checked(receivedSampleCount + 1);

            if (!framesByStackId.TryGetValue(sample.StackId, out var stackFrames))
            {
                stackFrames = await _store.GetStackFramesAsync(sample.StackId, cancellationToken).ConfigureAwait(false);
                framesByStackId.Add(sample.StackId, stackFrames);
            }

            MutableCallTreeNode? currentTreeNode = null;
            MutableHotspot? leafHotspot = null;
            foreach (var descriptor in stackFrames)
            {
                var frame = GetOrAddCoreFrame(coreFramesByDescriptor, descriptor);
                var hotspot = GetOrAddHotspot(hotspotsByDescriptor, descriptor, frame, ref nextFirstSeenOrder);
                hotspot.InclusiveSampleCount = checked(hotspot.InclusiveSampleCount + 1);

                currentTreeNode = currentTreeNode is null
                    ? GetOrAddRoot(callTreeRootsByDescriptor, descriptor, frame, ref nextFirstSeenOrder)
                    : currentTreeNode.GetOrAddChild(descriptor, frame, ref nextFirstSeenOrder);
                currentTreeNode.InclusiveSampleCount = checked(currentTreeNode.InclusiveSampleCount + 1);
                leafHotspot = hotspot;
            }

            if (currentTreeNode is not null && leafHotspot is not null)
            {
                currentTreeNode.ExclusiveSampleCount = checked(currentTreeNode.ExclusiveSampleCount + 1);
                leafHotspot.ExclusiveSampleCount = checked(leafHotspot.ExclusiveSampleCount + 1);
            }
        }

        var hotspots = hotspotsByDescriptor.Values
            .OrderByDescending(hotspot => hotspot.InclusiveSampleCount)
            .ThenByDescending(hotspot => hotspot.ExclusiveSampleCount)
            .ThenBy(hotspot => hotspot.Frame.MethodName, StringComparer.Ordinal)
            .ThenBy(hotspot => hotspot.Frame.ModuleName, StringComparer.Ordinal)
            .ThenBy(hotspot => hotspot.FirstSeenOrder)
            .Select(hotspot => new ExecutionHotspot(
                hotspot.Frame,
                hotspot.InclusiveSampleCount,
                hotspot.ExclusiveSampleCount))
            .ToArray();
        var callTreeRoots = callTreeRootsByDescriptor.Values
            .OrderBy(node => node.FirstSeenOrder)
            .Select(ToExecutionCallTreeNode)
            .ToArray();

        return new ExecutionProfile(range, receivedSampleCount, lostEventCount, hotspots, callTreeRoots);
    }

    private static ExecutionFrame GetOrAddCoreFrame(
        Dictionary<ExecutionFrameDescriptor, ExecutionFrame> framesByDescriptor,
        ExecutionFrameDescriptor descriptor)
    {
        if (framesByDescriptor.TryGetValue(descriptor, out var existingFrame))
        {
            return existingFrame;
        }

        var frame = new ExecutionFrame(descriptor.MethodName, descriptor.ModuleName, sourceLocation: null);
        framesByDescriptor.Add(descriptor, frame);
        return frame;
    }

    private static MutableHotspot GetOrAddHotspot(
        Dictionary<ExecutionFrameDescriptor, MutableHotspot> hotspotsByDescriptor,
        ExecutionFrameDescriptor descriptor,
        ExecutionFrame frame,
        ref long nextFirstSeenOrder)
    {
        if (hotspotsByDescriptor.TryGetValue(descriptor, out var existingHotspot))
        {
            return existingHotspot;
        }

        var hotspot = new MutableHotspot(frame, nextFirstSeenOrder++);
        hotspotsByDescriptor.Add(descriptor, hotspot);
        return hotspot;
    }

    private static MutableCallTreeNode GetOrAddRoot(
        Dictionary<ExecutionFrameDescriptor, MutableCallTreeNode> rootsByDescriptor,
        ExecutionFrameDescriptor descriptor,
        ExecutionFrame frame,
        ref long nextFirstSeenOrder)
    {
        if (rootsByDescriptor.TryGetValue(descriptor, out var existingRoot))
        {
            return existingRoot;
        }

        var root = new MutableCallTreeNode(frame, nextFirstSeenOrder++);
        rootsByDescriptor.Add(descriptor, root);
        return root;
    }

    private static ExecutionCallTreeNode ToExecutionCallTreeNode(MutableCallTreeNode node) =>
        new(
            node.Frame,
            node.InclusiveSampleCount,
            node.ExclusiveSampleCount,
            node.ChildrenByDescriptor.Values
                .OrderBy(child => child.FirstSeenOrder)
                .Select(ToExecutionCallTreeNode)
                .ToArray());

    private sealed class MutableHotspot
    {
        public MutableHotspot(ExecutionFrame frame, long firstSeenOrder)
        {
            Frame = frame;
            FirstSeenOrder = firstSeenOrder;
        }

        public ExecutionFrame Frame { get; }

        public long FirstSeenOrder { get; }

        public long InclusiveSampleCount { get; set; }

        public long ExclusiveSampleCount { get; set; }
    }

    private sealed class MutableCallTreeNode
    {
        public MutableCallTreeNode(ExecutionFrame frame, long firstSeenOrder)
        {
            Frame = frame;
            FirstSeenOrder = firstSeenOrder;
        }

        public ExecutionFrame Frame { get; }

        public long FirstSeenOrder { get; }

        public long InclusiveSampleCount { get; set; }

        public long ExclusiveSampleCount { get; set; }

        public Dictionary<ExecutionFrameDescriptor, MutableCallTreeNode> ChildrenByDescriptor { get; } = [];

        public MutableCallTreeNode GetOrAddChild(
            ExecutionFrameDescriptor descriptor,
            ExecutionFrame frame,
            ref long nextFirstSeenOrder)
        {
            if (ChildrenByDescriptor.TryGetValue(descriptor, out var existingChild))
            {
                return existingChild;
            }

            var child = new MutableCallTreeNode(frame, nextFirstSeenOrder++);
            ChildrenByDescriptor.Add(descriptor, child);
            return child;
        }
    }
}
