using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Application.Contracts.Diagnostics;

namespace DotnetAnalysis.Orchestration;

/// <summary>
/// 拥有正式快照顺序、选择状态和分析句柄的多快照集合。
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "This public type owns an orchestration snapshot collection.")]
public sealed class SnapshotCollection : ISnapshotCollection
{
    private readonly IMemorySnapshotAnalysisService _analysisService;
    private readonly object _gate = new();
    private readonly List<MemorySnapshot> _snapshots = [];
    private readonly Dictionary<MemorySnapshotId, SnapshotAnalysis> _analyses = [];
    private bool _disposed;

    /// <summary>创建空的快照集合。</summary>
    /// <param name="analysisService">提供快照分析的 Application 服务。</param>
    public SnapshotCollection(IMemorySnapshotAnalysisService analysisService)
    {
        _analysisService = analysisService ?? throw new ArgumentNullException(nameof(analysisService));
    }

    /// <inheritdoc />
    public IReadOnlyList<MemorySnapshot> Snapshots
    {
        get { lock (_gate) return _snapshots.ToArray(); }
    }

    /// <inheritdoc />
    public MemorySnapshot? CurrentSnapshot { get; private set; }

    /// <inheritdoc />
    public MemorySnapshot? BaselineSnapshot { get; private set; }

    /// <inheritdoc />
    public MemorySnapshot? CandidateSnapshot { get; private set; }

    /// <inheritdoc />
    public async Task<MemorySnapshot> AddCapturedAsync(
        Func<CancellationToken, Task<MemorySnapshot>> capture,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var snapshot = await capture(cancellationToken).ConfigureAwait(false);
        if (snapshot.State is MemorySnapshotState.Analyzing)
        {
            var analysis = await _analysisService.AnalyzeAsync(snapshot, cancellationToken).ConfigureAwait(false);
            snapshot = analysis.Snapshot;
        }
        return AddSnapshot(snapshot);
    }

    /// <inheritdoc />
    public MemorySnapshot AddImported(MemorySnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return AddSnapshot(snapshot);
    }

    /// <inheritdoc />
    public void SelectCurrent(MemorySnapshotId snapshotId)
    {
        lock (_gate) CurrentSnapshot = Find(snapshotId);
    }

    /// <inheritdoc />
    public void SelectBaseline(MemorySnapshotId snapshotId)
    {
        lock (_gate) BaselineSnapshot = Find(snapshotId);
    }

    /// <inheritdoc />
    public void SelectCandidate(MemorySnapshotId snapshotId)
    {
        lock (_gate) CandidateSnapshot = Find(snapshotId);
    }

    /// <inheritdoc />
    public ISnapshotAnalysis GetAnalysis(MemorySnapshotId snapshotId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var snapshot = Find(snapshotId);
            if (!_analyses.TryGetValue(snapshotId, out var analysis))
            {
                analysis = new SnapshotAnalysis(snapshot, _analysisService);
                _analyses.Add(snapshotId, analysis);
            }

            return analysis;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        SnapshotAnalysis[] analyses;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            analyses = _analyses.Values.ToArray();
            _analyses.Clear();
        }

        foreach (var analysis in analyses)
        {
            await analysis.DisposeAsync().ConfigureAwait(false);
        }
    }

    private MemorySnapshot AddSnapshot(MemorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.State is not MemorySnapshotState.Ready)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.CaptureFailed,
                "只有 Diagnostics 已正式持久化的 Ready 快照才能进入集合。");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_snapshots.Any(item => item.Id == snapshot.Id))
            {
                throw new ArgumentException("The snapshot is already registered.", nameof(snapshot));
            }

            _snapshots.Add(snapshot);
            CurrentSnapshot = snapshot;
            return snapshot;
        }
    }

    private MemorySnapshot Find(MemorySnapshotId snapshotId) =>
        _snapshots.FirstOrDefault(snapshot => snapshot.Id == snapshotId)
        ?? throw new KeyNotFoundException($"Snapshot '{snapshotId}' is not registered.");
}

