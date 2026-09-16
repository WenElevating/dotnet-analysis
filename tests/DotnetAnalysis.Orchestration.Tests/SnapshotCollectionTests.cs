using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Orchestration;

namespace DotnetAnalysis.Orchestration.Tests;

/// <summary>验证多快照集合的顺序、选择、捕获所有权和关闭边界。</summary>
[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe contract behavior.")]
public sealed class SnapshotCollectionTests
{
    [TestMethod]
    public async Task AddSnapshots_PreservesOrderAndSupportsSelections()
    {
        await using var collection = new SnapshotCollection(new FakeService());
        var first = Snapshot(MemorySnapshotOrigin.Captured);
        var second = Snapshot(MemorySnapshotOrigin.Imported);
        var third = Snapshot(MemorySnapshotOrigin.Captured);

        await collection.AddCapturedAsync(_ => Task.FromResult(first), CancellationToken.None);
        collection.AddImported(second);
        await collection.AddCapturedAsync(_ => Task.FromResult(third), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { first, second, third }, collection.Snapshots.ToArray());
        Assert.AreEqual(third, collection.CurrentSnapshot);
        collection.SelectCurrent(first.Id);
        collection.SelectBaseline(first.Id);
        collection.SelectCandidate(third.Id);
        Assert.AreEqual(first, collection.CurrentSnapshot);
        Assert.AreEqual(first, collection.BaselineSnapshot);
        Assert.AreEqual(third, collection.CandidateSnapshot);
    }

    [TestMethod]
    public async Task CaptureFailureOrCancellation_DoesNotEnterCollection()
    {
        await using var collection = new SnapshotCollection(new FakeService());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await collection.AddCapturedAsync(
            token => { token.ThrowIfCancellationRequested(); return Task.FromResult(Snapshot(MemorySnapshotOrigin.Captured)); }, cancelled.Token));
        await Assert.ThrowsAsync<DiagnosticsException>(async () => await collection.AddCapturedAsync(
            _ => Task.FromException<MemorySnapshot>(new DiagnosticsException(DiagnosticsErrorCode.CaptureFailed, "failed")), CancellationToken.None));
        Assert.IsEmpty(collection.Snapshots);
    }

    [TestMethod]
    public async Task Close_ReleasesAnalysisHandlesButKeepsSnapshots()
    {
        var service = new FakeService();
        await using var collection = new SnapshotCollection(service);
        var snapshot = Snapshot(MemorySnapshotOrigin.Captured);
        await collection.AddCapturedAsync(_ => Task.FromResult(snapshot), CancellationToken.None);
        var analysis = collection.GetAnalysis(snapshot.Id);
        await analysis.AnalyzeAsync(CancellationToken.None);
        await collection.DisposeAsync();
        Assert.AreEqual(snapshot, collection.Snapshots.Single());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await analysis.AnalyzeAsync(CancellationToken.None));
        Assert.AreEqual(1, service.AnalyzeCalls);
    }

    private static MemorySnapshot Snapshot(MemorySnapshotOrigin origin) => new(
        MemorySnapshotId.New(), origin, DateTimeOffset.UtcNow,
        origin == MemorySnapshotOrigin.Captured ? DateTimeOffset.UtcNow : null,
        DateTimeOffset.UtcNow, MemorySnapshotState.Ready);

    private sealed class FakeService : IMemorySnapshotAnalysisService
    {
        public int AnalyzeCalls { get; private set; }
        public Task<MemorySnapshotAnalysis> AnalyzeAsync(MemorySnapshot snapshot, CancellationToken cancellationToken)
        {
            AnalyzeCalls++;
            return Task.FromResult(new MemorySnapshotAnalysis(snapshot, [], AllocationProfile.NotAvailable(snapshot.RequestedAtUtc, snapshot.RequestedAtUtc)));
        }
        public Task<IReadOnlyList<MemoryObjectInfo>> GetObjectsAsync(MemorySnapshot snapshot, TypeIdentity type, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MemoryObjectInfo>>([]);
        public Task<MemoryObjectPage> GetObjectsPageAsync(MemorySnapshot snapshot, TypeIdentity type, int offset, int pageSize, CancellationToken cancellationToken) => Task.FromResult(new MemoryObjectPage([], 0, offset, pageSize));
        public Task<MemoryReferencePath?> GetReferencePathAsync(MemorySnapshot snapshot, ulong objectAddress, CancellationToken cancellationToken) => Task.FromResult<MemoryReferencePath?>(null);
    }
}

