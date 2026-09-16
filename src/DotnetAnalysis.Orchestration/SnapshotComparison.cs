using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Orchestration;

/// <summary>
/// 独立拥有基线和候选快照的比较操作；比较失败不会改变任一快照的基础分析状态。
/// </summary>
public sealed class SnapshotComparison
{
    private readonly IMemorySnapshotAnalysisService _analysisService;
    private int _disposed;

    /// <summary>创建基线与候选快照比较。</summary>
    /// <param name="baseline">比较基线。</param>
    /// <param name="candidate">比较候选。</param>
    /// <param name="analysisService">应用层快照分析服务。</param>
    public SnapshotComparison(
        MemorySnapshot baseline,
        MemorySnapshot candidate,
        IMemorySnapshotAnalysisService analysisService)
    {
        Baseline = baseline ?? throw new ArgumentNullException(nameof(baseline));
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        _analysisService = analysisService ?? throw new ArgumentNullException(nameof(analysisService));
    }

    /// <summary>比较基线快照。</summary>
    public MemorySnapshot Baseline { get; }

    /// <summary>比较候选快照。</summary>
    public MemorySnapshot Candidate { get; }

    /// <summary>执行比较并原样传播底层错误码和结果质量。</summary>
    /// <param name="cancellationToken">取消当前比较的令牌。</param>
    /// <returns>独立的快照比较结果。</returns>
    public Task<MemorySnapshotComparison> AnalyzeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _analysisService.CompareSnapshotsAsync(Baseline, Candidate, cancellationToken);
    }

    internal void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
