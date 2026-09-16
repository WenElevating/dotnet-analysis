using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Orchestration;

/// <summary>
/// 将单个正式快照的基础和派生查询转发至 Application 分析契约，并隔离共享分析缓存。
/// </summary>
public sealed class SnapshotAnalysis : ISnapshotAnalysis
{
    private readonly IMemorySnapshotAnalysisService _analysisService;
    private readonly object _gate = new();
    private Task<MemorySnapshotAnalysis>? _analysisTask;
    private bool _disposed;

    /// <summary>创建单快照分析句柄。</summary>
    /// <param name="snapshot">已正式保存的快照。</param>
    /// <param name="analysisService">Application 层分析服务。</param>
    public SnapshotAnalysis(MemorySnapshot snapshot, IMemorySnapshotAnalysisService analysisService)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _analysisService = analysisService ?? throw new ArgumentNullException(nameof(analysisService));
    }

    /// <inheritdoc />
    public MemorySnapshot Snapshot { get; }

    /// <inheritdoc />
    public Task<MemorySnapshotAnalysis> AnalyzeAsync(CancellationToken cancellationToken)
    {
        var task = GetOrCreateAnalysisTask();
        return task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryObjectInfo>> GetObjectsAsync(TypeIdentity type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(type);
        var analysis = await AnalyzeAsync(cancellationToken).ConfigureAwait(false);
        if (analysis.Types.Sum(static item => item.ObjectCount) >= MemorySnapshotAnalysis.FullObjectEnumerationLimit)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.SnapshotTooLargeForFullEnumeration,
                "快照对象数达到完整枚举上限，请改用分页查询。");
        }

        return await _analysisService.GetObjectsAsync(Snapshot, type, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<MemoryObjectPage> GetObjectsPageAsync(TypeIdentity type, int offset, int pageSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _analysisService.GetObjectsPageAsync(Snapshot, type, offset, pageSize, cancellationToken);
    }

    /// <inheritdoc />
    public Task<MemoryReferencePath?> GetReferencePathAsync(ulong objectAddress, CancellationToken cancellationToken) =>
        _analysisService.GetReferencePathAsync(Snapshot, objectAddress, cancellationToken);

    /// <inheritdoc />
    public Task<MemoryRetentionPathResult?> GetRetentionPathsAsync(ulong objectAddress, int maxPathCount, CancellationToken cancellationToken) =>
        _analysisService.GetRetentionPathsAsync(Snapshot, objectAddress, maxPathCount, cancellationToken);

    /// <inheritdoc />
    public Task<MemoryDominatorPage> GetDominatorPageAsync(int offset, int pageSize, CancellationToken cancellationToken) =>
        _analysisService.GetDominatorPageAsync(Snapshot, offset, pageSize, cancellationToken);

    /// <inheritdoc />
    public SnapshotComparison CompareWith(ISnapshotAnalysis candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate is not SnapshotAnalysis candidateAnalysis)
        {
            throw new ArgumentException("候选分析必须由同一快照编排实现提供。", nameof(candidate));
        }

        ThrowIfDisposed();
        candidateAnalysis.ThrowIfDisposed();
        return new SnapshotComparison(Snapshot, candidateAnalysis.Snapshot, _analysisService);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            _analysisTask = null;
        }

        return ValueTask.CompletedTask;
    }

    private Task<MemorySnapshotAnalysis> GetOrCreateAnalysisTask()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _analysisTask ??= _analysisService.AnalyzeAsync(Snapshot, CancellationToken.None);
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

