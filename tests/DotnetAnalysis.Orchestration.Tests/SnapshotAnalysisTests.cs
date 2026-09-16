using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Orchestration;

namespace DotnetAnalysis.Orchestration.Tests;

/// <summary>验证单快照分析的分页门禁、派生隔离、取消和比较转发。</summary>
[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe contract behavior.")]
public sealed class SnapshotAnalysisTests
{
    [TestMethod]
    public async Task FullEnumerationAtLimit_UsesPagingError()
    {
        var service = new FakeService { Analysis = CreateAnalysis(100_000) };
        await using var analysis = new SnapshotAnalysis(Snapshot(), service);
        var exception = await Assert.ThrowsAsync<DiagnosticsException>(async () => await analysis.GetObjectsAsync(new TypeIdentity("Sample.Type", "Sample"), CancellationToken.None));
        Assert.AreEqual(DiagnosticsErrorCode.SnapshotTooLargeForFullEnumeration, exception.ErrorCode);
        Assert.AreEqual(0, service.GetObjectsCalls);
        var page = await analysis.GetObjectsPageAsync(new TypeIdentity("Sample.Type", "Sample"), 0, 1000, CancellationToken.None);
        Assert.AreEqual(1, service.GetObjectsPageCalls);
        Assert.AreEqual(100_000, page.TotalObjectCount);
    }

    [TestMethod]
    public async Task DerivedFailure_DoesNotInvalidateBaseAnalysis()
    {
        var service = new FakeService
        {
            Analysis = CreateAnalysis(1),
            DominatorException = new DiagnosticsException(DiagnosticsErrorCode.DerivedAnalysisUnavailable, "derived failed")
        };
        await using var analysis = new SnapshotAnalysis(Snapshot(), service);
        var expected = await analysis.AnalyzeAsync(CancellationToken.None);
        var exception = await Assert.ThrowsAsync<DiagnosticsException>(async () => await analysis.GetDominatorPageAsync(0, 10, CancellationToken.None));
        var actual = await analysis.AnalyzeAsync(CancellationToken.None);
        Assert.AreEqual(DiagnosticsErrorCode.DerivedAnalysisUnavailable, exception.ErrorCode);
        Assert.AreEqual(expected, actual);
        Assert.AreEqual(1, service.AnalyzeCalls);
    }

    [TestMethod]
    public async Task QueryCancellation_CancelsOnlyCallerAndKeepsSharedAnalysis()
    {
        var service = new FakeService { Analysis = CreateAnalysis(1) };
        service.PendingAnalysis = new TaskCompletionSource<MemorySnapshotAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var analysis = new SnapshotAnalysis(Snapshot(), service);
        using var cancelled = new CancellationTokenSource();
        var pending = analysis.AnalyzeAsync(cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
        service.PendingAnalysis.SetResult(service.Analysis);
        var result = await analysis.AnalyzeAsync(CancellationToken.None);
        Assert.AreEqual(1, service.AnalyzeCalls);
        Assert.AreEqual(service.Analysis, result);
    }

    [TestMethod]
    public async Task CompareWith_ReturnsIndependentComparisonAndPropagatesFailure()
    {
        var service = new FakeService { Comparison = new MemorySnapshotComparison(Snapshot().Id, Snapshot().Id, []) };
        await using var baseline = new SnapshotAnalysis(Snapshot(), service);
        await using var candidate = new SnapshotAnalysis(Snapshot(), service);
        var comparison = baseline.CompareWith(candidate);
        Assert.AreEqual(service.Comparison, await comparison.AnalyzeAsync(CancellationToken.None));
        service.ComparisonException = new DiagnosticsException(DiagnosticsErrorCode.SnapshotQueryLimitReached, "comparison failed");
        await Assert.ThrowsAsync<DiagnosticsException>(async () => await comparison.AnalyzeAsync(CancellationToken.None));
    }

    private static MemorySnapshotAnalysis CreateAnalysis(long count)
    {
        var snapshot = Snapshot();
        return new MemorySnapshotAnalysis(snapshot, [new MemoryTypeSummary(new TypeIdentity("Sample.Type", "Sample"), count, count * 8)], AllocationProfile.NotAvailable(snapshot.RequestedAtUtc, snapshot.RequestedAtUtc));
    }

    private static MemorySnapshot Snapshot() => new(MemorySnapshotId.New(), MemorySnapshotOrigin.Imported, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, MemorySnapshotState.Ready);

    private sealed class FakeService : IMemorySnapshotAnalysisService
    {
        public MemorySnapshotAnalysis Analysis { get; init; } = CreateAnalysis(0);
        public MemorySnapshotComparison Comparison { get; init; } = new(Snapshot().Id, Snapshot().Id, []);
        public Exception? DominatorException { get; init; }
        public Exception? ComparisonException { get; set; }
        public TaskCompletionSource<MemorySnapshotAnalysis>? PendingAnalysis { get; set; }
        public int AnalyzeCalls { get; private set; }
        public int GetObjectsCalls { get; private set; }
        public int GetObjectsPageCalls { get; private set; }
        public Task<MemorySnapshotAnalysis> AnalyzeAsync(MemorySnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AnalyzeCalls++;
            return PendingAnalysis?.Task ?? Task.FromResult(Analysis);
        }
        public Task<IReadOnlyList<MemoryObjectInfo>> GetObjectsAsync(MemorySnapshot snapshot, TypeIdentity type, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); GetObjectsCalls++; return Task.FromResult<IReadOnlyList<MemoryObjectInfo>>([]);
        }
        public Task<MemoryObjectPage> GetObjectsPageAsync(MemorySnapshot snapshot, TypeIdentity type, int offset, int pageSize, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); GetObjectsPageCalls++; return Task.FromResult(new MemoryObjectPage([], Analysis.Types.Sum(item => item.ObjectCount), offset, pageSize));
        }
        public Task<MemoryReferencePath?> GetReferencePathAsync(MemorySnapshot snapshot, ulong objectAddress, CancellationToken cancellationToken) => Task.FromResult<MemoryReferencePath?>(null);
        public Task<MemoryDominatorPage> GetDominatorPageAsync(MemorySnapshot snapshot, int offset, int pageSize, CancellationToken cancellationToken) => DominatorException is null ? Task.FromResult(new MemoryDominatorPage([], 0, offset, pageSize)) : Task.FromException<MemoryDominatorPage>(DominatorException);
        public Task<MemorySnapshotComparison> CompareSnapshotsAsync(MemorySnapshot baselineSnapshot, MemorySnapshot candidateSnapshot, CancellationToken cancellationToken) => ComparisonException is null ? Task.FromResult(Comparison) : Task.FromException<MemorySnapshotComparison>(ComparisonException);
    }
}
