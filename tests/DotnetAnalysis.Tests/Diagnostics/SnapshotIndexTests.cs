using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Tests.Diagnostics;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class SnapshotIndexTests
{
    [TestMethod]
    public void GetObjects_WhenIndexIsLarge_RejectsFullEnumerationWithStableError()
    {
        var type = new TypeIdentity("Sample.Type", "Sample");
        var index = new SnapshotIndex(Enumerable.Range(0, 100_000)
            .Select(value => new SnapshotIndex.ObjectRow((ulong)value + 1, type, 16))
            .ToArray());

        var exception = Assert.ThrowsExactly<DiagnosticsException>(() => index.GetObjects(type));

        Assert.AreEqual(DiagnosticsErrorCode.SnapshotTooLargeForFullEnumeration, exception.ErrorCode);
    }

    [TestMethod]
    public void GetPage_ProjectsOnlyRequestedTypeRange()
    {
        var first = new TypeIdentity("First", "Sample");
        var second = new TypeIdentity("Second", "Sample");
        var index = new SnapshotIndex(
        [
            new SnapshotIndex.ObjectRow(1, first, 16),
            new SnapshotIndex.ObjectRow(2, second, 16),
            new SnapshotIndex.ObjectRow(3, first, 16),
            new SnapshotIndex.ObjectRow(4, first, 16)
        ]);

        var page = index.GetPage(first, 1, 1);

        Assert.AreEqual(3L, page.TotalObjectCount);
        Assert.AreEqual(3UL, page.Objects.Single().Address);
        Assert.IsTrue(page.HasNextPage);
    }

    [TestMethod]
    public async Task Cache_UsesSingleFlightAndDoesNotCancelSharedParse()
    {
        var cache = new SnapshotIndexCache();
        var snapshotId = MemorySnapshotId.New();
        var gate = new TaskCompletionSource<SnapshotIndex>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<SnapshotIndex> LoadAsync()
        {
            calls++;
            return gate.Task;
        }

        using var cancellation = new CancellationTokenSource();
        var cancelled = cache.GetAsync(snapshotId, LoadAsync, cancellation.Token);
        var concurrent = cache.GetAsync(snapshotId, LoadAsync, CancellationToken.None);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelled);

        var expected = new SnapshotIndex([]);
        gate.SetResult(expected);

        Assert.AreSame(expected, await concurrent);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task Cache_RetriesAfterFailedParse()
    {
        var cache = new SnapshotIndexCache();
        var snapshotId = MemorySnapshotId.New();
        var calls = 0;

        Task<SnapshotIndex> LoadAsync()
        {
            calls++;
            return calls == 1
                ? Task.FromException<SnapshotIndex>(new InvalidDataException("bad dump"))
                : Task.FromResult(new SnapshotIndex([]));
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            async () => await cache.GetAsync(snapshotId, LoadAsync, CancellationToken.None));
        _ = await cache.GetAsync(snapshotId, LoadAsync, CancellationToken.None);

        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task Cache_ReplacesIdleIndexWhenSnapshotChanges()
    {
        var cache = new SnapshotIndexCache();
        var firstSnapshot = MemorySnapshotId.New();
        var secondSnapshot = MemorySnapshotId.New();

        _ = await cache.GetAsync(firstSnapshot, () => Task.FromResult(new SnapshotIndex([])), CancellationToken.None);
        _ = await cache.GetAsync(secondSnapshot, () => Task.FromResult(new SnapshotIndex([])), CancellationToken.None);

        Assert.AreEqual(secondSnapshot, cache.CurrentSnapshotId);
    }

    [TestMethod]
    public async Task Cache_WhenSnapshotChanges_WaitsForTheExistingColdParseBeforeStartingAnother()
    {
        var cache = new SnapshotIndexCache();
        var firstSnapshot = MemorySnapshotId.New();
        var secondSnapshot = MemorySnapshotId.New();
        var firstGate = new TaskCompletionSource<SnapshotIndex>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCalls = 0;

        var first = cache.GetAsync(
            firstSnapshot,
            () =>
            {
                firstStarted.TrySetResult();
                return firstGate.Task;
            },
            CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = cache.GetAsync(
            secondSnapshot,
            () =>
            {
                secondCalls++;
                return Task.FromResult(new SnapshotIndex([]));
            },
            CancellationToken.None);

        await Task.Delay(50);
        Assert.AreEqual(0, secondCalls);
        firstGate.SetResult(new SnapshotIndex([]));
        _ = await first;
        _ = await second;
        Assert.AreEqual(1, secondCalls);
    }

    [TestMethod]
    public async Task ReferencePath_ConcurrentQueriesBuildReverseIndexOnlyOnce()
    {
        var type = new TypeIdentity("Node", "Sample");
        var firstBuilderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowFirstBuilderToFinish = new ManualResetEventSlim();
        var index = new SnapshotIndex(
        [
            new SnapshotIndex.ObjectRow(1, type, 16),
            new SnapshotIndex.ObjectRow(2, type, 16)
        ],
        new Dictionary<ulong, IReadOnlyList<ulong>> { [1] = [2] },
        [1],
        () =>
        {
            firstBuilderEntered.TrySetResult();
            allowFirstBuilderToFinish.Wait(TimeSpan.FromSeconds(2));
        });

        var first = Task.Run(() => index.GetReferencePath(2));
        await firstBuilderEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = Task.Run(() => index.GetReferencePath(2));
        allowFirstBuilderToFinish.Set();
        await Task.WhenAll(first, second);

        Assert.AreEqual(1, index.ReverseIndexBuildCount);
    }

    [TestMethod]
    public void ReferencePath_WhenObjectHasNoGcRoot_ReturnsNullInsteadOfAnUnrootedChain()
    {
        var type = new TypeIdentity("Node", "Sample");
        var index = new SnapshotIndex(
        [
            new SnapshotIndex.ObjectRow(1, type, 16),
            new SnapshotIndex.ObjectRow(2, type, 16)
        ],
        new Dictionary<ulong, IReadOnlyList<ulong>> { [1] = [2] });

        var path = index.GetReferencePath(2);

        Assert.IsNull(path);
    }
}
