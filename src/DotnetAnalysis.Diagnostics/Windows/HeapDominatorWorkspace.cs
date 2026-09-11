using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using DotnetAnalysis.Application.Contracts.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 将 objectId 到 DFS 编号的映射和 Lengauer-Tarjan 节点状态保存在单个临时文件映射中，
/// 使构建期托管堆占用不随快照对象数线性增长。工作区只属于一次派生构建，发布前必须释放并删除。
/// </summary>
internal sealed class HeapDominatorWorkspace : IDisposable
{
    private const int DfsNumberBytes = sizeof(int);
    private const int NodeRecordBytes = sizeof(int) * 10 + sizeof(long);
    private const int ObjectIdOffset = 0;
    private const int ParentOffset = ObjectIdOffset + sizeof(int);
    private const int SemiOffset = ParentOffset + sizeof(int);
    private const int ImmediateDominatorOffset = SemiOffset + sizeof(int);
    private const int AncestorOffset = ImmediateDominatorOffset + sizeof(int);
    private const int LabelOffset = AncestorOffset + sizeof(int);
    private const int BucketHeadOffset = LabelOffset + sizeof(int);
    private const int BucketNextOffset = BucketHeadOffset + sizeof(int);
    private const int ScratchOffset = BucketNextOffset + sizeof(int);
    private const int IsRootOffset = ScratchOffset + sizeof(int);
    private const int RetainedSizeOffset = IsRootOffset + sizeof(int);
    private readonly MemoryMappedFile _mapping;
    private readonly HeapMappedFileWindow _dfsWindow;
    private readonly HeapMappedFileWindow _nodeWindow;
    private readonly long _nodeTableOffset;
    private bool _disposed;

    /// <summary>
    /// 创建固定长度的可写临时工作区；在预算校验通过前不会创建文件或映射。
    /// </summary>
    /// <param name="path">本次派生构建独占且尚不存在的工作区文件。</param>
    /// <param name="objectCount">基础索引对象数。</param>
    /// <param name="budgetBytes">允许该工作区占用的最大字节数。</param>
    /// <returns>可随机访问 objectId 映射和 DFS 节点记录的文件工作区。</returns>
    /// <exception cref="DiagnosticsException">工作区长度超过预算时引发。</exception>
    public static HeapDominatorWorkspace Create(
        string path,
        int objectCount,
        long budgetBytes,
        long maximumWindowBytes = HeapIndexResourcePolicy.DefaultMappedReadWindowBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(objectCount);
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        var length = CalculateLengthBytes(objectCount);
        if (length > budgetBytes)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.DerivedAnalysisUnavailable,
                "当前可用资源不足以创建快照支配树文件工作区。");
        }

        var fullPath = Path.GetFullPath(path);
        return HeapTemporaryArtifactCleanup.CreateFileAndOpen(
            fullPath,
            length,
            () => new HeapDominatorWorkspace(fullPath, objectCount, maximumWindowBytes));
    }

    /// <summary>
    /// 计算一个对象规模对应的单文件工作区长度，包含 objectId 映射和虚拟根所需的 DFS 记录。
    /// </summary>
    /// <param name="objectCount">基础索引对象数。</param>
    /// <returns>工作区固定长度字节数。</returns>
    public static long CalculateLengthBytes(int objectCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(objectCount);
        return checked((long)objectCount * DfsNumberBytes + ((long)objectCount + 2) * NodeRecordBytes);
    }

    /// <summary>
    /// 读取对象在非递归 DFS 中分配的编号；零表示尚未从非弱根到达。
    /// </summary>
    /// <param name="objectId">基础索引中的连续对象标识。</param>
    /// <returns>DFS 编号或零。</returns>
    public int GetDfsNumber(int objectId)
    {
        ValidateObjectId(objectId);
        return _dfsWindow.ReadInt32((long)objectId * DfsNumberBytes);
    }

    /// <summary>
    /// 写入对象到 DFS 编号的唯一映射。
    /// </summary>
    /// <param name="objectId">基础索引中的连续对象标识。</param>
    /// <param name="dfsNumber">已分配的正 DFS 编号。</param>
    public void SetDfsNumber(int objectId, int dfsNumber)
    {
        ValidateObjectId(objectId);
        ArgumentOutOfRangeException.ThrowIfLessThan(dfsNumber, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dfsNumber, ObjectCount + 1);
        _dfsWindow.WriteInt32((long)objectId * DfsNumberBytes, dfsNumber);
    }

    /// <summary>
    /// 一次性读取一个 DFS 节点的全部 LT 状态，避免调用方为每个字段维护独立 O(N) 容器。
    /// </summary>
    /// <param name="dfsNumber">包含虚拟根在内的正 DFS 编号。</param>
    /// <returns>节点的当前可变状态副本。</returns>
    public HeapDominatorNodeState ReadNode(int dfsNumber)
    {
        var offset = GetNodeOffset(dfsNumber);
        return _nodeWindow.ReadRecord<HeapDominatorNodeRecord>(offset).ToState();
    }

    /// <summary>
    /// 将一个 DFS 节点的完整状态写回固定记录；调用方负责维持 LT 状态转换的一致性。
    /// </summary>
    /// <param name="dfsNumber">包含虚拟根在内的正 DFS 编号。</param>
    /// <param name="state">要覆盖写入的节点状态。</param>
    public void WriteNode(int dfsNumber, HeapDominatorNodeState state)
    {
        var offset = GetNodeOffset(dfsNumber);
        _nodeWindow.WriteRecord(offset, new HeapDominatorNodeRecord(state));
    }

    /// <summary>
    /// 只更新节点的 DFS 边游标或 Evaluate 临时链，避免单字段变化覆盖整条记录。
    /// </summary>
    /// <param name="dfsNumber">包含虚拟根在内的正 DFS 编号。</param>
    /// <param name="scratch">新的临时字段值。</param>
    public void WriteScratch(int dfsNumber, int scratch)
        => _nodeWindow.WriteInt32(GetNodeOffset(dfsNumber) + ScratchOffset, scratch);

    /// <summary>
    /// 只更新节点是否具有非弱根证据的标记。
    /// </summary>
    /// <param name="dfsNumber">包含虚拟根在内的正 DFS 编号。</param>
    /// <param name="isRoot">节点拥有非弱根证据时为 <see langword="true"/>。</param>
    public void WriteIsRoot(int dfsNumber, bool isRoot)
        => _nodeWindow.WriteInt32(GetNodeOffset(dfsNumber) + IsRootOffset, isRoot ? 1 : 0);

    /// <summary>
    /// 一次更新 LT 主循环为当前节点确定的 semi、ancestor 和桶链后继字段。
    /// </summary>
    /// <param name="dfsNumber">要更新的 DFS 节点编号。</param>
    /// <param name="semi">节点的半支配者 DFS 编号。</param>
    /// <param name="ancestor">并查集祖先 DFS 编号。</param>
    /// <param name="bucketNext">节点在半支配者桶链中的下一项。</param>
    public void WriteLinkedState(int dfsNumber, int semi, int ancestor, int bucketNext)
    {
        var offset = GetNodeOffset(dfsNumber);
        _nodeWindow.WriteInt32(offset + SemiOffset, semi);
        _nodeWindow.WriteInt32(offset + AncestorOffset, ancestor);
        _nodeWindow.WriteInt32(offset + BucketNextOffset, bucketNext);
    }

    /// <summary>
    /// 只更新一个半支配者桶的首节点编号。
    /// </summary>
    /// <param name="dfsNumber">拥有桶链的 DFS 节点编号。</param>
    /// <param name="bucketHead">新的桶链首节点编号；零表示空桶。</param>
    public void WriteBucketHead(int dfsNumber, int bucketHead)
        => _nodeWindow.WriteInt32(GetNodeOffset(dfsNumber) + BucketHeadOffset, bucketHead);

    /// <summary>
    /// 只更新节点的直接支配者 DFS 编号。
    /// </summary>
    /// <param name="dfsNumber">要更新的 DFS 节点编号。</param>
    /// <param name="immediateDominator">新的直接支配者 DFS 编号。</param>
    public void WriteImmediateDominator(int dfsNumber, int immediateDominator)
        => _nodeWindow.WriteInt32(GetNodeOffset(dfsNumber) + ImmediateDominatorOffset, immediateDominator);

    /// <summary>
    /// 一次写回 Evaluate 路径压缩产生的 ancestor、label 和 scratch 状态。
    /// </summary>
    /// <param name="dfsNumber">要压缩的 DFS 节点编号。</param>
    /// <param name="ancestor">压缩后的并查集祖先 DFS 编号。</param>
    /// <param name="label">压缩后的最小半支配标签 DFS 编号。</param>
    /// <param name="scratch">压缩后保留的临时字段，正常完成时为零。</param>
    public void WriteEvaluatedState(int dfsNumber, int ancestor, int label, int scratch)
    {
        var offset = GetNodeOffset(dfsNumber);
        _nodeWindow.WriteInt32(offset + AncestorOffset, ancestor);
        _nodeWindow.WriteInt32(offset + LabelOffset, label);
        _nodeWindow.WriteInt32(offset + ScratchOffset, scratch);
    }

    /// <summary>
    /// 只更新节点累计的保留大小。
    /// </summary>
    /// <param name="dfsNumber">要更新的 DFS 节点编号。</param>
    /// <param name="retainedSizeBytes">新的非负保留大小。</param>
    public void WriteRetainedSize(int dfsNumber, long retainedSizeBytes)
        => _nodeWindow.WriteInt64(GetNodeOffset(dfsNumber) + RetainedSizeOffset, retainedSizeBytes);

    /// <summary>
    /// 释放映射视图和工作区文件句柄；工作区文件由创建本次构建的上层在其专属临时目录内删除。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _nodeWindow.Dispose();
        _dfsWindow.Dispose();
        _mapping.Dispose();
    }

    /// <summary>
    /// 打开已按精确长度创建的工作区映射。
    /// </summary>
    private HeapDominatorWorkspace(string path, int objectCount, long maximumWindowBytes)
    {
        ObjectCount = objectCount;
        _nodeTableOffset = (long)objectCount * DfsNumberBytes;
        var length = CalculateLengthBytes(objectCount);
        _mapping = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, length, MemoryMappedFileAccess.ReadWrite);
        try
        {
            _dfsWindow = new HeapMappedFileWindow(_mapping, length, MemoryMappedFileAccess.ReadWrite, maximumWindowBytes);
            var maximumNodeCachedWindows = maximumWindowBytes <= HeapIndexResourcePolicy.MaximumMappedWindowBytes / 2
                ? 2
                : 1;
            _nodeWindow = new HeapMappedFileWindow(
                _mapping,
                length,
                MemoryMappedFileAccess.ReadWrite,
                maximumWindowBytes,
                maximumNodeCachedWindows);
        }
        catch
        {
            _mapping.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 单个活动工作区视图允许的最大字节数。
    /// </summary>
    public long MaximumMappedWindowBytes => _dfsWindow.MaximumMappedWindowBytes;

    /// <summary>
    /// 当前活动工作区视图的实际字节数；尚未访问时为零。
    /// </summary>
    public long CurrentMappedWindowBytes => Math.Max(
        _dfsWindow.CurrentMappedWindowBytes,
        _nodeWindow.CurrentMappedWindowBytes);

    /// <summary>节点窗口执行的整记录读取次数。</summary>
    public long NodeRecordReadOperations => _nodeWindow.RecordReadOperations;

    /// <summary>节点窗口执行的整记录写入次数。</summary>
    public long NodeRecordWriteOperations => _nodeWindow.RecordWriteOperations;

    /// <summary>节点窗口执行的标量读取次数。</summary>
    public long NodeScalarReadOperations => _nodeWindow.ScalarReadOperations;

    /// <summary>节点窗口执行的标量写入次数。</summary>
    public long NodeScalarWriteOperations => _nodeWindow.ScalarWriteOperations;

    /// <summary>节点窗口因未命中活动窗口而创建映射视图的次数。</summary>
    public long NodeViewOpenOperations => _nodeWindow.ViewOpenOperations;

    /// <summary>
    /// 工作区对应的基础对象数。
    /// </summary>
    private int ObjectCount { get; }

    /// <summary>
    /// 验证并计算一个 DFS 固定记录的文件偏移。
    /// </summary>
    private long GetNodeOffset(int dfsNumber)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(dfsNumber, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dfsNumber, ObjectCount + 1);
        return checked(_nodeTableOffset + (long)dfsNumber * NodeRecordBytes);
    }

    /// <summary>
    /// 验证对象标识属于基础索引并确认工作区尚未释放。
    /// </summary>
    private void ValidateObjectId(int objectId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)objectId >= (uint)ObjectCount)
        {
            throw new ArgumentOutOfRangeException(nameof(objectId), objectId, "Object id must be inside the workspace object range.");
        }
    }

    /// <summary>
    /// 与工作区二进制布局一一对应的非托管节点记录，使完整状态只需一次映射访问即可读写。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct HeapDominatorNodeRecord
    {
        public readonly int ObjectId;
        public readonly int Parent;
        public readonly int Semi;
        public readonly int ImmediateDominator;
        public readonly int Ancestor;
        public readonly int Label;
        public readonly int BucketHead;
        public readonly int BucketNext;
        public readonly int Scratch;
        public readonly int IsRoot;
        public readonly long RetainedSizeBytes;

        /// <summary>
        /// 将算法使用的节点状态转换为固定二进制记录。
        /// </summary>
        /// <param name="state">要持久化到工作区的节点状态。</param>
        public HeapDominatorNodeRecord(HeapDominatorNodeState state)
        {
            ObjectId = state.ObjectId;
            Parent = state.Parent;
            Semi = state.Semi;
            ImmediateDominator = state.ImmediateDominator;
            Ancestor = state.Ancestor;
            Label = state.Label;
            BucketHead = state.BucketHead;
            BucketNext = state.BucketNext;
            Scratch = state.Scratch;
            IsRoot = state.IsRoot ? 1 : 0;
            RetainedSizeBytes = state.RetainedSizeBytes;
        }

        /// <summary>
        /// 将固定二进制记录还原为算法使用的节点状态。
        /// </summary>
        /// <returns>包含所有 LT 字段的节点状态。</returns>
        public HeapDominatorNodeState ToState() => new(
            ObjectId,
            Parent,
            Semi,
            ImmediateDominator,
            Ancestor,
            Label,
            BucketHead,
            BucketNext,
            Scratch,
            IsRoot != 0,
            RetainedSizeBytes);
    }
}

/// <summary>
/// 表示单个 DFS 节点在非递归 Lengauer-Tarjan、桶链和 retained size 累计阶段共享的固定宽度状态。
/// </summary>
/// <param name="ObjectId">基础索引对象标识；虚拟根使用对象数作为专用值。</param>
/// <param name="Parent">DFS 父节点编号。</param>
/// <param name="Semi">当前半支配者 DFS 编号。</param>
/// <param name="ImmediateDominator">当前直接支配者 DFS 编号。</param>
/// <param name="Ancestor">并查集祖先 DFS 编号。</param>
/// <param name="Label">并查集最小半支配标签 DFS 编号。</param>
/// <param name="BucketHead">以当前节点为半支配者的桶链首项。</param>
/// <param name="BucketNext">当前节点在桶链中的下一项。</param>
/// <param name="Scratch">DFS 边游标或 Evaluate 临时反向链。</param>
/// <param name="IsRoot">对象是否拥有非弱根证据。</param>
/// <param name="RetainedSizeBytes">当前累计保留大小。</param>
internal readonly record struct HeapDominatorNodeState(
    int ObjectId,
    int Parent,
    int Semi,
    int ImmediateDominator,
    int Ancestor,
    int Label,
    int BucketHead,
    int BucketNext,
    int Scratch,
    bool IsRoot,
    long RetainedSizeBytes);
