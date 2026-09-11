using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Tests.Diagnostics;

/// <summary>
/// 验证映射支配树后台构建的单飞、等待取消、所有者释放和虚拟根语义。
/// </summary>
[TestClass]
[DoNotParallelize]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "测试名称描述场景。")]
public sealed class MappedDominatorLifecycleTests
{
    /// <summary>
    /// 同一句柄的多个并发分页调用必须等待同一次后台构建，并在发布后得到一致结果且不产生冲突归档。
    /// </summary>
    [TestMethod]
    public async Task GetDominatorPageAsync_WhenCallersAreConcurrent_SharesOnePublishedBuild()
    {
        var root = CreateTestDirectory("Concurrent");
        HeapIndexHandle? handle = null;
        HeapArtifactPublicationGate.PublicationLease? gate = null;
        try
        {
            (handle, var derivedDirectory) = await CreateMappedHandleAsync(root);
            gate = await HeapArtifactPublicationGate.EnterAsync(derivedDirectory, CancellationToken.None);
            var calls = Enumerable.Range(0, 8)
                .Select(_ => handle.GetDominatorPageAsync(0, 10, CancellationToken.None))
                .ToArray();

            Assert.IsFalse(Task.WhenAll(calls).IsCompleted);
            gate.Dispose();
            gate = null;
            var pages = await Task.WhenAll(calls);

            Assert.IsTrue(pages.All(page => page.TotalObjectCount == 4));
            Assert.IsTrue(File.Exists(Path.Combine(derivedDirectory, "manifest.json")));
            Assert.IsEmpty(Directory.GetDirectories(Path.GetDirectoryName(derivedDirectory)!, ".heapderived.corrupt.*"));
        }
        finally
        {
            gate?.Dispose();
            handle?.Dispose();
            DeleteTestDirectory(root);
        }
    }

    /// <summary>
    /// 一个分页调用取消只应结束该等待者；由句柄拥有的后台构建在门闩释放后仍须供其他调用者复用。
    /// </summary>
    [TestMethod]
    public async Task GetDominatorPageAsync_WhenOneCallerCancels_DoesNotCancelSharedBuild()
    {
        var root = CreateTestDirectory("CallerCancellation");
        HeapIndexHandle? handle = null;
        HeapArtifactPublicationGate.PublicationLease? gate = null;
        try
        {
            (handle, var derivedDirectory) = await CreateMappedHandleAsync(root);
            gate = await HeapArtifactPublicationGate.EnterAsync(derivedDirectory, CancellationToken.None);
            using var cancellationSource = new CancellationTokenSource();
            var cancelledWaiter = handle.GetDominatorPageAsync(0, 10, cancellationSource.Token);
            cancellationSource.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelledWaiter);
            var survivingWaiter = handle.GetDominatorPageAsync(0, 10, CancellationToken.None);
            gate.Dispose();
            gate = null;
            var page = await survivingWaiter;

            Assert.AreEqual(4L, page.TotalObjectCount);
            Assert.IsTrue(File.Exists(Path.Combine(derivedDirectory, "manifest.json")));
        }
        finally
        {
            gate?.Dispose();
            handle?.Dispose();
            DeleteTestDirectory(root);
        }
    }

    /// <summary>
    /// 唯一等待者先取消而共享构建随后失败时，故障任务必须由完成路径自行移出缓存；
    /// 修复基础工件后的第一次新查询应直接启动新构建，而不是重放一次无人观察的旧故障。
    /// </summary>
    [TestMethod]
    public async Task GetDominatorPageAsync_WhenCancelledWaiterLeavesFailedBuild_ImmediatelyRetriesAfterRepair()
    {
        var root = CreateTestDirectory("CancelledWaiterFailedBuild");
        HeapIndexHandle? handle = null;
        HeapArtifactPublicationGate.PublicationLease? gate = null;
        try
        {
            (handle, var derivedDirectory) = await CreateMappedHandleAsync(root);
            var heapIndexDirectory = Path.GetDirectoryName(derivedDirectory)!;
            var forwardOffsetsPath = Path.Combine(heapIndexDirectory, "forward-offsets.bin");
            var validForwardOffsets = await File.ReadAllBytesAsync(forwardOffsetsPath);
            gate = await HeapArtifactPublicationGate.EnterAsync(derivedDirectory, CancellationToken.None);
            using var cancellationSource = new CancellationTokenSource();
            var cancelledWaiter = handle.GetDominatorPageAsync(0, 10, cancellationSource.Token);
            cancellationSource.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelledWaiter);
            await File.WriteAllBytesAsync(forwardOffsetsPath, []);
            gate.Dispose();
            gate = null;
            using (await HeapArtifactPublicationGate.EnterAsync(derivedDirectory, CancellationToken.None))
            {
                // 排在后台构建之后重新取得门闩，即证明损坏 CSR 对应的共享构建已经退出。
            }
            await File.WriteAllBytesAsync(forwardOffsetsPath, validForwardOffsets);

            var page = await handle.GetDominatorPageAsync(0, 10, CancellationToken.None);

            Assert.AreEqual(4L, page.TotalObjectCount);
            Assert.IsTrue(File.Exists(Path.Combine(derivedDirectory, "manifest.json")));
        }
        finally
        {
            gate?.Dispose();
            handle?.Dispose();
            DeleteTestDirectory(root);
        }
    }

    /// <summary>
    /// 释放映射句柄必须取消其仍在等待发布门闩的派生构建，并且不得删除或改名任何已发布基础证据。
    /// </summary>
    [TestMethod]
    public async Task Dispose_WhenDerivedBuildIsPending_CancelsOwnerWithoutPublishingPartialArtifact()
    {
        var root = CreateTestDirectory("DisposeCancellation");
        HeapIndexHandle? handle = null;
        HeapArtifactPublicationGate.PublicationLease? gate = null;
        try
        {
            (handle, var derivedDirectory) = await CreateMappedHandleAsync(root);
            var heapIndexDirectory = Path.GetDirectoryName(derivedDirectory)!;
            var baseManifestPath = Path.Combine(heapIndexDirectory, "manifest.json");
            var baseManifestBytes = await File.ReadAllBytesAsync(baseManifestPath);
            gate = await HeapArtifactPublicationGate.EnterAsync(derivedDirectory, CancellationToken.None);
            var query = handle.GetDominatorPageAsync(0, 10, CancellationToken.None);

            handle.Dispose();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await query);

            Assert.IsFalse(Directory.Exists(derivedDirectory));
            CollectionAssert.AreEqual(baseManifestBytes, await File.ReadAllBytesAsync(baseManifestPath));
            Assert.IsEmpty(Directory.GetDirectories(heapIndexDirectory, ".heapderived.*.tmp"));
        }
        finally
        {
            gate?.Dispose();
            handle?.Dispose();
            DeleteTestDirectory(root);
        }
    }

    /// <summary>
    /// 多个非弱根必须共同连接到虚拟根，弱根独占对象必须保持不可达；映射结果应与常驻实现逐对象一致。
    /// </summary>
    [TestMethod]
    public async Task GetDominatorPageAsync_WhenGraphHasMultipleAndWeakRoots_MatchesVirtualRootSemantics()
    {
        var root = CreateTestDirectory("VirtualRoot");
        try
        {
            var index = CreateSemanticIndex();
            var expected = index.GetDominatorPage(0, 10).Objects.OrderBy(item => item.ObjectInfo.Address).ToArray();
            var layout = new SnapshotStorageLayout(root);
            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 1, maximumInMemoryBytes: long.MaxValue);
            using var handle = await router.RouteAsync(MemorySnapshotId.New(), index, CancellationToken.None);

            var actual = (await handle.GetDominatorPageAsync(0, 10, CancellationToken.None))
                .Objects
                .OrderBy(item => item.ObjectInfo.Address)
                .ToArray();

            Assert.HasCount(4, actual);
            Assert.IsFalse(actual.Any(item => item.ObjectInfo.Address == 5));
            for (var indexValue = 0; indexValue < expected.Length; indexValue++)
            {
                Assert.AreEqual(expected[indexValue].ObjectInfo.Address, actual[indexValue].ObjectInfo.Address);
                Assert.AreEqual(expected[indexValue].ImmediateDominatorAddress, actual[indexValue].ImmediateDominatorAddress);
                Assert.AreEqual(expected[indexValue].RetainedSizeBytes, actual[indexValue].RetainedSizeBytes);
            }
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    /// <summary>
    /// 创建映射基础索引句柄并返回其尚未发布的派生目录。
    /// </summary>
    private static async Task<(HeapIndexHandle Handle, string DerivedDirectory)> CreateMappedHandleAsync(string root)
    {
        var layout = new SnapshotStorageLayout(root);
        var snapshotId = MemorySnapshotId.New();
        var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 1, maximumInMemoryBytes: long.MaxValue);
        var handle = await router.RouteAsync(snapshotId, CreateSemanticIndex(), CancellationToken.None);
        return (handle, Path.Combine(layout.GetHeapIndexDirectory(snapshotId), ".heapderived"));
    }

    /// <summary>
    /// 构造两个强根共享一个环、另有一个弱根独占对象的手算图。
    /// </summary>
    private static SnapshotIndex CreateSemanticIndex()
    {
        var type = new TypeIdentity("Sample.Node", "Sample");
        return new SnapshotIndex(
        [
            new SnapshotIndex.ObjectRow(1, type, 10),
            new SnapshotIndex.ObjectRow(2, type, 20),
            new SnapshotIndex.ObjectRow(3, type, 30),
            new SnapshotIndex.ObjectRow(4, type, 40),
            new SnapshotIndex.ObjectRow(5, type, 50)
        ],
        new Dictionary<ulong, IReadOnlyList<ulong>>
        {
            [1] = [3],
            [2] = [3],
            [3] = [4],
            [4] = [3]
        },
        retentionRoots:
        [
            new SnapshotIndex.RetentionRootRow(
                1,
                new MemoryRetentionRoot(MemoryRootKind.Stack, MemoryRootFlags.StackRoot, "Sample.Root.One", "Sample")),
            new SnapshotIndex.RetentionRootRow(
                2,
                new MemoryRetentionRoot(MemoryRootKind.Handle, MemoryRootFlags.None, null, null)),
            new SnapshotIndex.RetentionRootRow(
                5,
                new MemoryRetentionRoot(MemoryRootKind.Handle, MemoryRootFlags.WeakReference, null, null))
        ]);
    }

    /// <summary>
    /// 创建当前测试独占的快照根目录。
    /// </summary>
    private static string CreateTestDirectory(string scenario)
    {
        var path = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.MappedDominator.{scenario}.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// 删除当前测试独占目录，不触碰仓库或其他快照证据。
    /// </summary>
    private static void DeleteTestDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
