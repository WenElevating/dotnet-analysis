using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 将固定读取边界内的执行采样流式聚合为调用树和热点。
/// </summary>
internal sealed class ExecutionProfileBuilder
{
    private readonly ExecutionCaptureStore _store;
    private readonly Action? _beforeHotspotComparison;
    private readonly Action? _beforeCallTreeNodeMaterialization;
    private readonly Func<ExecutionFrameReference, CancellationToken, Task<SourceLocation?>>? _sourceLocationResolver;

    /// <summary>
    /// 创建执行采样聚合器。
    /// </summary>
    /// <param name="store">提供固定边界样本和调用栈帧的会话存储。</param>
    public ExecutionProfileBuilder(
        ExecutionCaptureStore store,
        Action? beforeHotspotComparison = null,
        Action? beforeCallTreeNodeMaterialization = null,
        Func<ExecutionFrameReference, CancellationToken, Task<SourceLocation?>>? sourceLocationResolver = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _beforeHotspotComparison = beforeHotspotComparison;
        _beforeCallTreeNodeMaterialization = beforeCallTreeNodeMaterialization;
        _sourceLocationResolver = sourceLocationResolver;
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
        CancellationToken cancellationToken) => await BuildCoreAsync(
            range,
            boundary,
            lostEventCount,
            useIncrementalSummaries: false,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 在固定读取边界内合并封存段摘要并按时间范围构建执行采样分析结果。
    /// </summary>
    /// <param name="range">要聚合的半开 UTC 时间范围。</param>
    /// <param name="boundary">调用方已固定的读取边界。</param>
    /// <param name="lostEventCount">同一查询边界内累计的丢失事件数。</param>
    /// <param name="cancellationToken">取消流式读取和聚合的令牌。</param>
    /// <returns>按稳定顺序排列热点且包含调用树的执行分析结果。</returns>
    public async Task<ExecutionProfile> BuildIncrementalAsync(
        ExecutionTimeRange range,
        ExecutionCaptureReadBoundary boundary,
        long lostEventCount,
        CancellationToken cancellationToken) => await BuildCoreAsync(
            range,
            boundary,
            lostEventCount,
            useIncrementalSummaries: true,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 在固定读取边界内执行共享聚合流程，并根据查询模式选择逐条读取或封存摘要加速。
    /// </summary>
    private async Task<ExecutionProfile> BuildCoreAsync(
        ExecutionTimeRange range,
        ExecutionCaptureReadBoundary boundary,
        long lostEventCount,
        bool useIncrementalSummaries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentOutOfRangeException.ThrowIfNegative(lostEventCount);
        cancellationToken.ThrowIfCancellationRequested();

        var coreFramesById = new Dictionary<int, ExecutionFrame>();
        var hotspotsByFrameId = new Dictionary<int, MutableHotspot>();
        var callTreeRootsByFrameId = new Dictionary<int, MutableCallTreeNode>();
        var nextFirstSeenOrder = 0L;
        var stackSampleCounts = useIncrementalSummaries
            ? await _store.ReadIncrementalStackSampleCountsAsync(
                range,
                boundary,
                cancellationToken).ConfigureAwait(false)
            : await _store.ReadStackSampleCountsAsync(
                range,
                boundary,
                cancellationToken).ConfigureAwait(false);
        foreach (var stackSampleCount in stackSampleCounts.Stacks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stackFrames = await _store.GetStackFramesAsync(
                stackSampleCount.StackId,
                cancellationToken).ConfigureAwait(false);

            MutableCallTreeNode? currentTreeNode = null;
            MutableHotspot? leafHotspot = null;
            for (var frameIndex = 0; frameIndex < stackFrames.Count; frameIndex++)
            {
                var frameReference = stackFrames[frameIndex];
                if (!coreFramesById.TryGetValue(frameReference.FrameId, out var frame))
                {
                    frame = await CreateCoreFrameAsync(frameReference, cancellationToken).ConfigureAwait(false);
                    coreFramesById.Add(frameReference.FrameId, frame);
                }

                var hotspot = GetOrAddHotspot(
                    hotspotsByFrameId,
                    frameReference.FrameId,
                    frame,
                    ref nextFirstSeenOrder);
                hotspot.InclusiveSampleCount = checked(
                    hotspot.InclusiveSampleCount + stackSampleCount.SampleCount);

                currentTreeNode = currentTreeNode is null
                    ? GetOrAddRoot(
                        callTreeRootsByFrameId,
                        frameReference.FrameId,
                        frame,
                        ref nextFirstSeenOrder)
                    : currentTreeNode.GetOrAddChild(
                        frameReference.FrameId,
                        frame,
                        ref nextFirstSeenOrder);
                currentTreeNode.InclusiveSampleCount = checked(
                    currentTreeNode.InclusiveSampleCount + stackSampleCount.SampleCount);
                leafHotspot = hotspot;
            }

            if (currentTreeNode is not null && leafHotspot is not null)
            {
                currentTreeNode.ExclusiveSampleCount = checked(
                    currentTreeNode.ExclusiveSampleCount + stackSampleCount.SampleCount);
                leafHotspot.ExclusiveSampleCount = checked(
                    leafHotspot.ExclusiveSampleCount + stackSampleCount.SampleCount);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var mutableHotspots = hotspotsByFrameId.Values.ToList();
        SortMutableHotspots(mutableHotspots, _beforeHotspotComparison, cancellationToken);
        var hotspots = new ExecutionHotspot[mutableHotspots.Count];
        for (var index = 0; index < mutableHotspots.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hotspot = mutableHotspots[index];
            hotspots[index] = new ExecutionHotspot(
                hotspot.Frame,
                hotspot.InclusiveSampleCount,
                hotspot.ExclusiveSampleCount);
        }

        var mutableCallTreeRoots = callTreeRootsByFrameId.Values.ToList();
        SortCallTreeNodes(mutableCallTreeRoots, cancellationToken);
        var callTreeRoots = new ExecutionCallTreeNode[mutableCallTreeRoots.Count];
        for (var index = 0; index < mutableCallTreeRoots.Count; index++)
        {
            callTreeRoots[index] = ToExecutionCallTreeNode(
                mutableCallTreeRoots[index],
                _beforeCallTreeNodeMaterialization,
                cancellationToken);
        }

        return new ExecutionProfile(
            range,
            stackSampleCounts.ReceivedSampleCount,
            lostEventCount,
            hotspots,
            callTreeRoots);
    }

    /// <summary>
    /// 将采样存储帧解析为领域帧，并仅在本地符号可用时附加源码位置。
    /// </summary>
    private async Task<ExecutionFrame> CreateCoreFrameAsync(
        ExecutionFrameReference frameReference,
        CancellationToken cancellationToken)
    {
        var sourceLocation = _sourceLocationResolver is null
            ? null
            : await _sourceLocationResolver(frameReference, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = frameReference.Descriptor;
        return new ExecutionFrame(descriptor.MethodName, descriptor.ModuleName, sourceLocation);
    }

    /// <summary>
    /// 按帧标识复用热点累加器，避免每个样本产生新的热点对象。
    /// </summary>
    private static MutableHotspot GetOrAddHotspot(
        Dictionary<int, MutableHotspot> hotspotsByFrameId,
        int frameId,
        ExecutionFrame frame,
        ref long nextFirstSeenOrder)
    {
        if (hotspotsByFrameId.TryGetValue(frameId, out var existingHotspot))
        {
            return existingHotspot;
        }

        var hotspot = new MutableHotspot(frame, nextFirstSeenOrder++);
        hotspotsByFrameId.Add(frameId, hotspot);
        return hotspot;
    }

    /// <summary>
    /// 获取或创建顶层调用树节点，并保持根节点首次出现的稳定顺序。
    /// </summary>
    private static MutableCallTreeNode GetOrAddRoot(
        Dictionary<int, MutableCallTreeNode> rootsByFrameId,
        int frameId,
        ExecutionFrame frame,
        ref long nextFirstSeenOrder)
    {
        if (rootsByFrameId.TryGetValue(frameId, out var existingRoot))
        {
            return existingRoot;
        }

        var root = new MutableCallTreeNode(frame, nextFirstSeenOrder++);
        rootsByFrameId.Add(frameId, root);
        return root;
    }

    /// <summary>
    /// 将内部可变调用树递归投影为不可变领域节点，并在投影前完成稳定排序。
    /// </summary>
    private static ExecutionCallTreeNode ToExecutionCallTreeNode(
        MutableCallTreeNode node,
        Action? beforeMaterialization,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        beforeMaterialization?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        var mutableChildren = node.ChildrenByFrameId.Values.ToList();
        SortCallTreeNodes(mutableChildren, cancellationToken);
        var children = new ExecutionCallTreeNode[mutableChildren.Count];
        for (var index = 0; index < mutableChildren.Count; index++)
        {
            children[index] = ToExecutionCallTreeNode(
                mutableChildren[index],
                beforeMaterialization,
                cancellationToken);
        }

        return new ExecutionCallTreeNode(
            node.Frame,
            node.InclusiveSampleCount,
            node.ExclusiveSampleCount,
            children);
    }

    /// <summary>
    /// 按首次出现序号递归排序调用树，保证相同输入得到稳定展示顺序。
    /// </summary>
    private static void SortCallTreeNodes(
        List<MutableCallTreeNode> nodes,
        CancellationToken cancellationToken)
    {
        try
        {
            nodes.Sort((left, right) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return left.FirstSeenOrder.CompareTo(right.FirstSeenOrder);
            });
        }
        catch (InvalidOperationException exception) when (exception.InnerException is OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    /// <summary>
    /// 按热点样本数和首次出现序号排序可变热点集合。
    /// </summary>
    private static void SortMutableHotspots(
        List<MutableHotspot> hotspots,
        Action? beforeComparison,
        CancellationToken cancellationToken)
    {
        try
        {
            hotspots.Sort(new MutableHotspotComparer(beforeComparison, cancellationToken));
        }
        catch (InvalidOperationException exception) when (exception.InnerException is OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    /// <summary>
    /// 聚合阶段使用的可变热点计数器，延后到输出时才创建不可变领域模型。
    /// </summary>
    private sealed class MutableHotspot
    {
        /// <summary>
        /// 以对应领域帧及其首次出现序号初始化热点累加器。
        /// </summary>
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

    /// <summary>
    /// 为热点输出定义按样本数降序、首次出现升序的稳定比较规则。
    /// </summary>
    private sealed class MutableHotspotComparer : IComparer<MutableHotspot>
    {
        private readonly CancellationToken _cancellationToken;
        private readonly Action? _beforeComparison;

        /// <summary>
        /// 绑定取消令牌与可选测试钩子，使比较期间也能响应取消。
        /// </summary>
        public MutableHotspotComparer(Action? beforeComparison, CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            _beforeComparison = beforeComparison;
        }

        /// <summary>
        /// 比较两个热点；空值仅用于满足比较器契约，实际聚合集合不包含空项。
        /// </summary>
        public int Compare(MutableHotspot? left, MutableHotspot? right)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _beforeComparison?.Invoke();
            _cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            var comparison = right.InclusiveSampleCount.CompareTo(left.InclusiveSampleCount);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = right.ExclusiveSampleCount.CompareTo(left.ExclusiveSampleCount);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(left.Frame.MethodName, right.Frame.MethodName);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(left.Frame.ModuleName, right.Frame.ModuleName);
            return comparison != 0
                ? comparison
                : left.FirstSeenOrder.CompareTo(right.FirstSeenOrder);
        }
    }

    /// <summary>
    /// 聚合期间按帧去重并累计样本数的可变调用树节点。
    /// </summary>
    private sealed class MutableCallTreeNode
    {
        /// <summary>
        /// 以领域帧和稳定首次出现序号初始化可变调用树节点。
        /// </summary>
        public MutableCallTreeNode(ExecutionFrame frame, long firstSeenOrder)
        {
            Frame = frame;
            FirstSeenOrder = firstSeenOrder;
        }

        public ExecutionFrame Frame { get; }

        public long FirstSeenOrder { get; }

        public long InclusiveSampleCount { get; set; }

        public long ExclusiveSampleCount { get; set; }

        public Dictionary<int, MutableCallTreeNode> ChildrenByFrameId { get; } = [];

        /// <summary>
        /// 获取或创建指定帧的子节点，并为首次出现的子节点分配稳定顺序。
        /// </summary>
        public MutableCallTreeNode GetOrAddChild(
            int frameId,
            ExecutionFrame frame,
            ref long nextFirstSeenOrder)
        {
            if (ChildrenByFrameId.TryGetValue(frameId, out var existingChild))
            {
                return existingChild;
            }

            var child = new MutableCallTreeNode(frame, nextFirstSeenOrder++);
            ChildrenByFrameId.Add(frameId, child);
            return child;
        }
    }
}
