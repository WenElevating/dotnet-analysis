using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;
using FastSerialization;
using Graphs;

namespace DotnetAnalysis.Tests.Diagnostics;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class SnapshotIndexTests
{
    /// <summary>
    /// FastSerialization 大快照必须拥有独立的磁盘 spool 入口，避免标签、blob 和地址表同时常驻托管内存。
    /// </summary>
    [TestMethod]
    public void GCDumpSnapshotReader_ExposesFastSerializationDiskSpoolBuilder()
    {
        var method = typeof(GCDumpSnapshotReader).GetMethod(
            "TryCreateFastSerializationSpool",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        Assert.IsNotNull(method);
    }

    /// <summary>
    /// 磁盘索引的反向 CSR 路径遍历必须逐条枚举父对象，不能先把高扇入节点的所有父对象复制为数组。
    /// </summary>
    [TestMethod]
    public void MappedHeapIndex_StreamsReverseParentsInsteadOfMaterializingAnArray()
    {
        var method = typeof(MappedHeapIndex).GetMethod(
            "ReadReverseParents",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        Assert.IsNotNull(method);
        Assert.AreEqual(typeof(IEnumerable<int>), method.ReturnType);
    }

    /// <summary>
    /// 映射索引的单次保留路径遍历必须复用受限 CSR 映射窗口，
    /// 不能为图遍历中的每个已访问对象重复打开偏移和目标工件文件。
    /// </summary>
    [TestMethod]
    public void MappedHeapIndex_UsesMappedWindowsForReverseParentTraversal()
    {
        var method = typeof(MappedHeapIndex).GetMethod(
            "ReadReverseParents",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: [typeof(int), typeof(HeapMappedReadWindow), typeof(HeapMappedReadWindow)],
            modifiers: null);

        Assert.IsNotNull(method);
        Assert.AreEqual(typeof(IEnumerable<int>), method.ReturnType);
    }

    /// <summary>
    /// 单个对象拥有大量根证据时，磁盘索引也必须逐条读取，不能为排序先构造完整根证据列表。
    /// </summary>
    [TestMethod]
    public void MappedHeapIndex_StreamsRootEvidenceInsteadOfMaterializingAList()
    {
        var readerType = typeof(MappedHeapIndex).GetNestedType(
            "RootEvidenceReader",
            System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(readerType);
        var method = readerType.GetMethod("Read", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);

        Assert.IsNotNull(method);
        Assert.AreEqual(typeof(IEnumerable<MemoryRetentionRoot>), method.ReturnType);
    }

    /// <summary>
    /// FastSerialization 大图必须直接由标签、blob 与地址 spool 发布映射工件，并保持分页和未知 GC 根路径的公开语义。
    /// </summary>
    [TestMethod]
    public async Task GCDumpSnapshotReader_BuildsMappedIndexFromFastSerializationSpool()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.GCDumpMapped.{Guid.NewGuid():N}");
        var snapshotPath = Path.Combine(root, "sample.gcdump");
        Directory.CreateDirectory(root);
        try
        {
            var type = new TypeIdentity("Sample.Node", "Sample");
            GCDumpFastSerializationWriter.Write(
                snapshotPath,
                new GCDumpSnapshotReader.HeapData(
                    [],
                    [
                        new MemoryObjectInfo(1, type, 24),
                        new MemoryObjectInfo(2, type, 32),
                        new MemoryObjectInfo(3, type, 48)
                    ],
                    new Dictionary<ulong, IReadOnlyList<ulong>>
                    {
                        [1] = [2],
                        [2] = [3]
                    },
                    [1]),
                new TargetProcess(4567, DateTimeOffset.UtcNow.AddMinutes(-1), "sample", null),
                DateTimeOffset.UtcNow,
                CancellationToken.None);

            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 2, maximumInMemoryBytes: long.MaxValue);
            var reader = (IIndexedMemorySnapshotReader)new GCDumpSnapshotReader();
            using var handle = await reader.BuildIndexAsync(snapshotId, snapshotPath, router, CancellationToken.None);

            Assert.IsTrue(handle.IsMapped);
            Assert.AreEqual(3L, handle.TypeSummaries.Single(summary => summary.Type == type).ObjectCount);
            CollectionAssert.AreEqual(new ulong[] { 2, 3 }, handle.GetPage(type, 1, 2).Objects.Select(item => item.Address).ToArray());
            var paths = handle.GetRetentionPaths(3, 1, CancellationToken.None);
            Assert.IsNotNull(paths);
            Assert.AreEqual(MemoryRootKind.Unknown, paths.Paths[0].Root.Kind);
            Assert.IsNull(paths.Paths[0].Root.FunctionName);
            CollectionAssert.AreEqual(new ulong[] { 1, 2, 3 }, paths.Paths[0].Objects.Select(item => item.Address).ToArray());
            Assert.IsFalse(Directory.Exists(Path.Combine(layout.GetHeapIndexDirectory(snapshotId), ".fastserialization-spool")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 外部 FastSerialization 文件的多个原始类型槽位若描述同一身份，磁盘工件必须规范化对象索引并合并统计。
    /// </summary>
    [TestMethod]
    public async Task GCDumpSnapshotReader_WhenRawTypesShareIdentity_MergesCanonicalType()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.GCDumpCanonicalType.{Guid.NewGuid():N}");
        var snapshotPath = Path.Combine(root, "sample.gcdump");
        Directory.CreateDirectory(root);
        try
        {
            WriteFastSerializationWithDuplicateTypes(snapshotPath);
            var type = new TypeIdentity("Sample.Node", "Sample");
            var layout = new SnapshotStorageLayout(root);
            var reader = (IIndexedMemorySnapshotReader)new GCDumpSnapshotReader();
            using var handle = await reader.BuildIndexAsync(
                MemorySnapshotId.New(),
                snapshotPath,
                new HeapIndexRouter(layout, maximumInMemoryObjectCount: 1, maximumInMemoryBytes: long.MaxValue),
                CancellationToken.None);

            var summary = handle.TypeSummaries.Single(item => item.Type == type);
            Assert.AreEqual(type, summary.Type);
            Assert.AreEqual(2L, summary.ObjectCount);
            Assert.AreEqual(56L, summary.TotalSizeBytes);
            CollectionAssert.AreEqual(
                new ulong[] { 1, 2 },
                handle.GetPage(type, 0, 2).Objects.Select(item => item.Address).ToArray());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// FastSerialization 类型表中的保留类型即使没有对象，也必须写入零统计，确保 v3 工件可热复用。
    /// </summary>
    [TestMethod]
    public async Task GCDumpSnapshotReader_WhenReservedTypesHaveNoObjects_PreservesZeroCountSummariesOnReopen()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.GCDumpZeroType.{Guid.NewGuid():N}");
        var snapshotPath = Path.Combine(root, "sample.gcdump");
        Directory.CreateDirectory(root);
        try
        {
            WriteFastSerializationWithZeroCountReservedTypes(snapshotPath);
            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            var reader = (IIndexedMemorySnapshotReader)new GCDumpSnapshotReader();
            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 1, maximumInMemoryBytes: long.MaxValue);

            using var first = await reader.BuildIndexAsync(snapshotId, snapshotPath, router, CancellationToken.None);
            Assert.IsTrue(first.IsMapped);
            Assert.HasCount(3, first.TypeSummaries);
            Assert.AreEqual(0L, first.TypeSummaries.Single(summary => summary.Type.TypeName == "UNDEFINED").ObjectCount);
            Assert.AreEqual(0L, first.TypeSummaries.Single(summary => summary.Type.TypeName == "[.NET Roots]").ObjectCount);

            using var reopened = await router.TryOpenExistingMappedAsync(snapshotId, CancellationToken.None);
            Assert.IsNotNull(reopened);
            Assert.HasCount(3, reopened.TypeSummaries);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 根偏移工件损坏导致读取器构造失败时，已经打开的偏移与证据流必须立即释放。
    /// </summary>
    [TestMethod]
    public async Task MappedHeapIndex_WhenRootOffsetsLengthIsInvalid_ReleasesOpenedEvidenceStreams()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.RootEvidenceLeak.{Guid.NewGuid():N}");
        try
        {
            var type = new TypeIdentity("Sample.Node", "Sample");
            var index = new SnapshotIndex(
                [new SnapshotIndex.ObjectRow(1, type, 16)],
                new Dictionary<ulong, IReadOnlyList<ulong>>(),
                roots: [1]);
            var directory = await BuildMappedArtifactAsync(root, index);
            var offsetsPath = Path.Combine(directory, "roots-by-object.bin");
            await File.WriteAllBytesAsync(offsetsPath, [0x01]);

            using var mapped = new MappedHeapIndex(directory);
            Assert.ThrowsExactly<InvalidDataException>(() => mapped.GetRetentionPaths(1, 1, CancellationToken.None));

            using var offsets = new FileStream(offsetsPath, FileMode.Open, FileAccess.Read, FileShare.None);
            using var evidence = new FileStream(Path.Combine(directory, "root-evidence.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// FastSerialization 根节点的 child 数量超过单字节压缩范围时，磁盘 spool 必须消费完整压缩数值而不能越过 blob 末尾。
    /// </summary>
    [TestMethod]
    public async Task GCDumpSnapshotReader_WhenRootHasMoreThan127Children_BuildsMappedIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.GCDumpLargeRoot.{Guid.NewGuid():N}");
        var snapshotPath = Path.Combine(root, "sample.gcdump");
        Directory.CreateDirectory(root);
        try
        {
            var type = new TypeIdentity("Sample.Node", "Sample");
            var objects = Enumerable.Range(1, 129)
                .Select(address => new MemoryObjectInfo((ulong)address, type, 24))
                .ToArray();
            GCDumpFastSerializationWriter.Write(
                snapshotPath,
                new GCDumpSnapshotReader.HeapData([], objects, new Dictionary<ulong, IReadOnlyList<ulong>>(), objects.Select(item => item.Address).ToArray()),
                new TargetProcess(4567, DateTimeOffset.UtcNow.AddMinutes(-1), "sample", null),
                DateTimeOffset.UtcNow,
                CancellationToken.None);

            var layout = new SnapshotStorageLayout(root);
            var reader = (IIndexedMemorySnapshotReader)new GCDumpSnapshotReader();
            using var handle = await reader.BuildIndexAsync(
                MemorySnapshotId.New(),
                snapshotPath,
                new HeapIndexRouter(layout, maximumInMemoryObjectCount: 2, maximumInMemoryBytes: long.MaxValue),
                CancellationToken.None);

            Assert.AreEqual(129L, handle.TypeSummaries.Single(summary => summary.Type == type).ObjectCount);
            var paths = handle.GetRetentionPaths(129, 1, CancellationToken.None);
            Assert.IsNotNull(paths);
            Assert.AreEqual(129UL, paths.Paths[0].Objects.Single().Address);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

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

    /// <summary>
    /// 常驻索引的地址映射必须保持一对一；重复地址不能静默绑定到第一条对象记录。
    /// </summary>
    [TestMethod]
    public void Constructor_WhenObjectAddressesAreDuplicated_ThrowsStableIndexBuildFailure()
    {
        var type = new TypeIdentity("Sample.Node", "Sample");

        var exception = Assert.ThrowsExactly<DiagnosticsException>(() => new SnapshotIndex(
        [
            new SnapshotIndex.ObjectRow(1, type, 16),
            new SnapshotIndex.ObjectRow(1, type, 24)
        ]));

        Assert.AreEqual(DiagnosticsErrorCode.SnapshotIndexBuildFailed, exception.ErrorCode);
        Assert.IsInstanceOfType<InvalidDataException>(exception.InnerException);
    }

    /// <summary>
    /// 常驻索引不得从引用图中静默删除指向快照外非零对象的边。
    /// </summary>
    [TestMethod]
    public void Constructor_WhenEdgeTargetIsUnknown_ThrowsStableIndexBuildFailure()
    {
        var type = new TypeIdentity("Sample.Node", "Sample");

        var exception = Assert.ThrowsExactly<DiagnosticsException>(() => new SnapshotIndex(
            [new SnapshotIndex.ObjectRow(1, type, 16)],
            new Dictionary<ulong, IReadOnlyList<ulong>> { [1] = [2] }));

        Assert.AreEqual(DiagnosticsErrorCode.SnapshotIndexBuildFailed, exception.ErrorCode);
        Assert.IsInstanceOfType<InvalidDataException>(exception.InnerException);
    }

    /// <summary>
    /// 常驻索引不得接受由快照外非零源地址声明的引用边。
    /// </summary>
    [TestMethod]
    public void Constructor_WhenEdgeSourceIsUnknown_ThrowsStableIndexBuildFailure()
    {
        var type = new TypeIdentity("Sample.Node", "Sample");

        var exception = Assert.ThrowsExactly<DiagnosticsException>(() => new SnapshotIndex(
            [new SnapshotIndex.ObjectRow(1, type, 16)],
            new Dictionary<ulong, IReadOnlyList<ulong>> { [2] = [1] }));

        Assert.AreEqual(DiagnosticsErrorCode.SnapshotIndexBuildFailed, exception.ErrorCode);
        Assert.IsInstanceOfType<InvalidDataException>(exception.InnerException);
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

    /// <summary>
    /// 自动路由句柄缓存也必须保持单飞、独立等待取消、失败可重试和快照切换时的串行冷构建语义。
    /// </summary>
    [TestMethod]
    public async Task HeapIndexHandleCache_PreservesSingleFlightCancellationRetryAndColdBuildSerialization()
    {
        using var cache = new HeapIndexHandleCache();
        var firstSnapshot = MemorySnapshotId.New();
        var secondSnapshot = MemorySnapshotId.New();
        var firstGate = new TaskCompletionSource<HeapIndexHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCalls = 0;
        var secondCalls = 0;

        Task<HeapIndexHandle> LoadFirstAsync()
        {
            firstCalls++;
            firstStarted.TrySetResult();
            return firstGate.Task;
        }

        using var cancellation = new CancellationTokenSource();
        var cancelledWaiter = cache.GetAsync(firstSnapshot, LoadFirstAsync, cancellation.Token);
        var activeWaiter = cache.GetAsync(firstSnapshot, LoadFirstAsync, CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelledWaiter);

        var secondWaiter = cache.GetAsync(
            secondSnapshot,
            () =>
            {
                secondCalls++;
                return Task.FromResult(HeapIndexHandle.CreateInMemory(new SnapshotIndex([])));
            },
            CancellationToken.None);
        await Task.Delay(50);
        Assert.AreEqual(0, secondCalls);

        var firstHandle = HeapIndexHandle.CreateInMemory(new SnapshotIndex([]));
        firstGate.SetResult(firstHandle);
        using var activeLease = await activeWaiter;
        using var secondLease = await secondWaiter;
        Assert.AreNotSame(firstHandle, activeLease);
        Assert.AreEqual(1, firstCalls);
        Assert.AreEqual(1, secondCalls);

        var failedSnapshot = MemorySnapshotId.New();
        var retryCalls = 0;
        Task<HeapIndexHandle> FailThenRetryAsync()
        {
            retryCalls++;
            return retryCalls == 1
                ? Task.FromException<HeapIndexHandle>(new InvalidDataException("bad index"))
                : Task.FromResult(HeapIndexHandle.CreateInMemory(new SnapshotIndex([])));
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            async () => await cache.GetAsync(failedSnapshot, FailThenRetryAsync, CancellationToken.None));
        using var retryLease = await cache.GetAsync(failedSnapshot, FailThenRetryAsync, CancellationToken.None);
        Assert.AreEqual(2, retryCalls);
    }

    /// <summary>
    /// 冷构建完成后缓存不得继续保存前序任务链，否则快照切换会保留历史任务和映射句柄。
    /// </summary>
    [TestMethod]
    public async Task HeapIndexHandleCache_WhenColdBuildCompletes_DoesNotRetainCompletedTaskTail()
    {
        using var cache = new HeapIndexHandleCache();
        using var lease = await cache.GetAsync(
            MemorySnapshotId.New(),
            () => Task.FromResult(HeapIndexHandle.CreateInMemory(new SnapshotIndex([]))),
            CancellationToken.None);

        var tailField = typeof(HeapIndexHandleCache).GetField(
            "_coldBuildTail",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.IsNull(tailField);
        var gateField = typeof(HeapIndexHandleCache).GetField(
            "_coldBuildGate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(gateField);
        Assert.AreEqual(1, ((SemaphoreSlim)gateField.GetValue(cache)!).CurrentCount);
    }

    /// <summary>
    /// 切换当前快照只能移除缓存所有权；已经返回给普通分页调用方的旧租约仍必须可查询。
    /// </summary>
    [TestMethod]
    public async Task HeapIndexHandleCache_WhenSnapshotSwitches_KeepsPriorOrdinaryQueryLeaseUsable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.HandleLeasePage.{Guid.NewGuid():N}");
        try
        {
            var type = new TypeIdentity("Sample.Node", "Sample");
            var index = new SnapshotIndex([new SnapshotIndex.ObjectRow(1, type, 16)]);
            var directory = await BuildMappedArtifactAsync(root, index);
            using var cache = new HeapIndexHandleCache();
            using var firstLease = await cache.GetAsync(
                MemorySnapshotId.New(),
                () => Task.FromResult(HeapIndexHandle.CreateMapped(directory)),
                CancellationToken.None);
            using var secondLease = await cache.GetAsync(
                MemorySnapshotId.New(),
                () => Task.FromResult(HeapIndexHandle.CreateInMemory(new SnapshotIndex([]))),
                CancellationToken.None);

            var page = firstLease.GetPage(type, 0, 1);

            Assert.AreEqual(1UL, page.Objects.Single().Address);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 旧快照的映射支配查询在缓存切换后必须继续等待；仅当最后一个调用方租约释放时才取消其所有者任务。
    /// </summary>
    [TestMethod]
    public async Task HeapIndexHandleCache_WhenSnapshotSwitches_CancelsMappedDominatorOnlyAfterLastLeaseIsReleased()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.HandleLeaseDominator.{Guid.NewGuid():N}");
        HeapArtifactPublicationGate.PublicationLease? publicationGate = null;
        HeapIndexHandle? firstLease = null;
        try
        {
            var type = new TypeIdentity("Sample.Node", "Sample");
            var index = new SnapshotIndex(
                [new SnapshotIndex.ObjectRow(1, type, 16)],
                roots: [1]);
            var directory = await BuildMappedArtifactAsync(root, index);
            var derivedDirectory = Path.Combine(directory, ".heapderived");
            using var cache = new HeapIndexHandleCache();
            firstLease = await cache.GetAsync(
                MemorySnapshotId.New(),
                () => Task.FromResult(HeapIndexHandle.CreateMapped(directory)),
                CancellationToken.None);
            publicationGate = await HeapArtifactPublicationGate.EnterAsync(derivedDirectory, CancellationToken.None);
            var query = firstLease.GetDominatorPageAsync(0, 1, CancellationToken.None);

            using var secondLease = await cache.GetAsync(
                MemorySnapshotId.New(),
                () => Task.FromResult(HeapIndexHandle.CreateInMemory(new SnapshotIndex([]))),
                CancellationToken.None);

            await Task.Delay(TimeSpan.FromMilliseconds(100));
            Assert.IsFalse(query.IsCompleted, "Cache eviction canceled an in-flight query that still owns a lease.");
            firstLease.Dispose();
            firstLease = null;
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await query);
        }
        finally
        {
            publicationGate?.Dispose();
            firstLease?.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
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

    [TestMethod]
    public void ReferencePath_WhenRootIndexIsLarge_DoesNotAllocateAHashSetForEachQuery()
    {
        const int rootCount = 100_000;
        var type = new TypeIdentity("Node", "Sample");
        var objects = Enumerable.Range(1, rootCount)
            .Select(address => new SnapshotIndex.ObjectRow((ulong)address, type, 16))
            .ToArray();
        var index = new SnapshotIndex(objects, roots: objects.Select(row => row.Address).ToArray());

        _ = index.GetReferencePath(1);
        var before = GC.GetAllocatedBytesForCurrentThread();

        var path = index.GetReferencePath(1);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsNotNull(path);
        Assert.IsLessThan(1_048_576L, allocated);
    }

    [TestMethod]
    public void RetentionPaths_WhenMultipleRootsReachTarget_OrdersVerifiedStackEvidenceBeforeOtherRootKinds()
    {
        var type = new TypeIdentity("Node", "Sample");
        var index = new SnapshotIndex(
        [
            new SnapshotIndex.ObjectRow(1, type, 16),
            new SnapshotIndex.ObjectRow(2, type, 16),
            new SnapshotIndex.ObjectRow(3, type, 16),
            new SnapshotIndex.ObjectRow(4, type, 16)
        ],
        new Dictionary<ulong, IReadOnlyList<ulong>>
        {
            [1] = [4],
            [2] = [4],
            [3] = [4]
        },
        retentionRoots:
        [
            new SnapshotIndex.RetentionRootRow(
                1,
                new MemoryRetentionRoot(
                    MemoryRootKind.Handle,
                    MemoryRootFlags.None,
                    null,
                    null)),
            new SnapshotIndex.RetentionRootRow(
                2,
                new MemoryRetentionRoot(
                    MemoryRootKind.Stack,
                    MemoryRootFlags.StackRoot,
                    "Sample.Holder.KeepAlive",
                    "Sample")),
            new SnapshotIndex.RetentionRootRow(
                3,
                new MemoryRetentionRoot(
                    MemoryRootKind.Stack,
                    MemoryRootFlags.StackRoot,
                    null,
                    null))
        ]);

        var result = index.GetRetentionPaths(4, maxPathCount: 16);

        Assert.IsNotNull(result);
        Assert.HasCount(3, result.Paths);
        Assert.AreEqual("Sample.Holder.KeepAlive", result.Paths[0].Root.FunctionName);
        Assert.AreEqual(MemoryRootKind.Stack, result.Paths[1].Root.Kind);
        Assert.IsNull(result.Paths[1].Root.FunctionName);
        Assert.AreEqual(MemoryRootKind.Handle, result.Paths[2].Root.Kind);
        Assert.AreEqual(2UL, result.Paths[0].Objects[0].Address);
        Assert.AreEqual(4UL, result.Paths[0].Objects[1].Address);
    }

    /// <summary>
    /// 弱引用只描述观察到的根标志，不构成对象存活证据，不能作为保留路径的起点。
    /// </summary>
    [TestMethod]
    public void RetentionPaths_WhenOnlyWeakRootReachesTarget_ReturnsNull()
    {
        var type = new TypeIdentity("Node", "Sample");
        var index = new SnapshotIndex(
        [
            new SnapshotIndex.ObjectRow(1, type, 16),
            new SnapshotIndex.ObjectRow(2, type, 16)
        ],
        new Dictionary<ulong, IReadOnlyList<ulong>> { [1] = [2] },
        retentionRoots:
        [
            new SnapshotIndex.RetentionRootRow(
                1,
                new MemoryRetentionRoot(
                    MemoryRootKind.Handle,
                    MemoryRootFlags.WeakReference,
                    null,
                    null))
        ]);

        Assert.IsNull(index.GetRetentionPaths(2, maxPathCount: 16));
    }

    /// <summary>
    /// 保留路径查询在开始构建反向索引前必须响应调用方取消，不能进入全图遍历。
    /// </summary>
    [TestMethod]
    public void RetentionPaths_WhenCancellationIsRequested_ThrowsBeforeTraversal()
    {
        var type = new TypeIdentity("Node", "Sample");
        var index = new SnapshotIndex(
        [
            new SnapshotIndex.ObjectRow(1, type, 16),
            new SnapshotIndex.ObjectRow(2, type, 16)
        ],
        new Dictionary<ulong, IReadOnlyList<ulong>> { [1] = [2] },
        roots: [1]);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(
            () => index.GetRetentionPaths(2, maxPathCount: 16, cancellationSource.Token));
    }

    /// <summary>
    /// 支配树必须以所有非弱根为虚拟超级根的子节点，并按保留大小而非浅表大小排序。
    /// </summary>
    [TestMethod]
    public void Dominators_WhenGraphContainsDiamond_ComputesImmediateDominatorAndRetainedSize()
    {
        var type = new TypeIdentity("Node", "Sample");
        var index = new SnapshotIndex(
        [
            new SnapshotIndex.ObjectRow(1, type, 10),
            new SnapshotIndex.ObjectRow(2, type, 20),
            new SnapshotIndex.ObjectRow(3, type, 30),
            new SnapshotIndex.ObjectRow(4, type, 40)
        ],
        new Dictionary<ulong, IReadOnlyList<ulong>>
        {
            [1] = [2, 3],
            [2] = [4],
            [3] = [4]
        },
        roots: [1]);

        var page = index.GetDominatorPage(offset: 0, pageSize: 10);

        Assert.AreEqual(4L, page.TotalObjectCount);
        var root = page.Objects.Single(item => item.ObjectInfo.Address == 1);
        var shared = page.Objects.Single(item => item.ObjectInfo.Address == 4);
        Assert.AreEqual(100L, root.RetainedSizeBytes);
        Assert.IsNull(root.ImmediateDominatorAddress);
        Assert.AreEqual(40L, shared.RetainedSizeBytes);
        Assert.AreEqual(1UL, shared.ImmediateDominatorAddress);
    }

    /// <summary>
    /// 多个根共同可达的环不应被任一根错误独占；环内对象仍必须有稳定的直接支配者和保留大小。
    /// </summary>
    [TestMethod]
    public void Dominators_WhenGraphContainsMultipleRootsAndCycle_DoesNotOvercountSharedRetention()
    {
        var type = new TypeIdentity("Node", "Sample");
        var index = new SnapshotIndex(
        [
            new SnapshotIndex.ObjectRow(1, type, 10),
            new SnapshotIndex.ObjectRow(2, type, 20),
            new SnapshotIndex.ObjectRow(3, type, 30),
            new SnapshotIndex.ObjectRow(4, type, 40)
        ],
        new Dictionary<ulong, IReadOnlyList<ulong>>
        {
            [1] = [3],
            [2] = [3],
            [3] = [4],
            [4] = [3]
        },
        roots: [1, 2]);

        var page = index.GetDominatorPage(0, 10);

        var firstRoot = page.Objects.Single(item => item.ObjectInfo.Address == 1);
        var secondRoot = page.Objects.Single(item => item.ObjectInfo.Address == 2);
        var shared = page.Objects.Single(item => item.ObjectInfo.Address == 3);
        Assert.AreEqual(10L, firstRoot.RetainedSizeBytes);
        Assert.AreEqual(20L, secondRoot.RetainedSizeBytes);
        Assert.AreEqual(70L, shared.RetainedSizeBytes);
        Assert.IsNull(shared.ImmediateDominatorAddress);
    }

    /// <summary>
    /// 超过内存对象预算的图必须自动发布不可变磁盘工件，并由映射句柄向上层提供同一查询契约。
    /// </summary>
    [TestMethod]
    public async Task HeapIndexRouter_WhenObjectBudgetIsExceeded_PublishesMappedArtifact()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.Router.{Guid.NewGuid():N}");
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            var type = new TypeIdentity("Node", "Sample");
            var index = new SnapshotIndex(
            [
                new SnapshotIndex.ObjectRow(1, type, 16),
                new SnapshotIndex.ObjectRow(2, type, 16),
                new SnapshotIndex.ObjectRow(3, type, 16)
            ],
            new Dictionary<ulong, IReadOnlyList<ulong>> { [1] = [2], [2] = [3] },
            roots: [1]);
            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 2, maximumInMemoryBytes: long.MaxValue);

            using var handle = await router.RouteAsync(snapshotId, index, CancellationToken.None);

            Assert.IsTrue(handle.IsMapped);
            Assert.IsFalse(handle.IsInMemoryGraphResident);
            Assert.IsTrue(File.Exists(Path.Combine(layout.GetHeapIndexDirectory(snapshotId), "manifest.json")));
            Assert.AreEqual(3L, handle.TypeSummaries.Single().ObjectCount);
            Assert.AreEqual(3UL, handle.GetPage(type, 2, 1).Objects.Single().Address);
            CollectionAssert.AreEqual(
                new ulong[] { 1, 2, 3 },
                handle.GetReferencePath(3, CancellationToken.None)!.Objects.Select(item => item.Address).ToArray());
            using var cancellationSource = new CancellationTokenSource();
            cancellationSource.Cancel();
            Assert.ThrowsExactly<OperationCanceledException>(
                () => handle.GetReferencePath(3, cancellationSource.Token));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 小快照即使因资源预算路由到磁盘索引，单一类型超过公开分页上限时也必须保留全量对象读取契约。
    /// </summary>
    [TestMethod]
    public async Task HeapIndexRouter_WhenSmallSnapshotIsMappedAndTypeExceedsPageLimit_ReturnsAllObjects()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.MappedFullEnumeration.{Guid.NewGuid():N}");
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            var type = new TypeIdentity("Node", "Sample");
            var objects = Enumerable.Range(1, 1_001)
                .Select(address => new SnapshotIndex.ObjectRow((ulong)address, type, 16))
                .ToArray();
            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 1, maximumInMemoryBytes: long.MaxValue);

            using var handle = await router.RouteAsync(
                snapshotId,
                new SnapshotIndex(objects),
                CancellationToken.None);

            Assert.IsTrue(handle.IsMapped);
            Assert.AreEqual(MemorySnapshotObjectAccessMode.Full, handle.ObjectAccessMode);
            var result = handle.GetObjects(type);
            Assert.HasCount(1_001, result);
            Assert.AreEqual(1UL, result[0].Address);
            Assert.AreEqual(1_001UL, result[^1].Address);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 新建磁盘索引必须发布计划约定的 v3 manifest；版本是工件恢复和损坏目录隔离的兼容边界。
    /// </summary>
    [TestMethod]
    public async Task HeapIndexRouter_WhenPublishingMappedArtifact_WritesVersion3Manifest()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.HeapIndexManifest.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var type = new TypeIdentity("Sample.Node", "Sample");
            var snapshotId = MemorySnapshotId.New();
            var layout = new SnapshotStorageLayout(root);
            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 1, maximumInMemoryBytes: long.MaxValue);
            using var handle = await router.RouteAsync(
                snapshotId,
                new SnapshotIndex([new SnapshotIndex.ObjectRow(1, type, 24)]),
                CancellationToken.None);

            await using var stream = File.OpenRead(Path.Combine(layout.GetHeapIndexDirectory(snapshotId), "manifest.json"));
            using var manifest = await JsonDocument.ParseAsync(stream);
            Assert.AreEqual(3, manifest.RootElement.GetProperty("Version").GetInt32());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 映射索引必须为每个对象写入根证据区间，路径查询不能反序列化整个快照的根证据集合。
    /// </summary>
    [TestMethod]
    public async Task HeapIndexRouter_WhenMappedPathIsQueried_StoresRootEvidenceByObjectRange()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.RootEvidence.{Guid.NewGuid():N}");
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            var type = new TypeIdentity("Node", "Sample");
            var index = new SnapshotIndex(
            [
                new SnapshotIndex.ObjectRow(1, type, 16),
                new SnapshotIndex.ObjectRow(2, type, 16),
                new SnapshotIndex.ObjectRow(3, type, 16)
            ],
            new Dictionary<ulong, IReadOnlyList<ulong>> { [1] = [2], [2] = [3] },
            retentionRoots:
            [
                new SnapshotIndex.RetentionRootRow(
                    1,
                    new MemoryRetentionRoot(
                        MemoryRootKind.Stack,
                        MemoryRootFlags.StackRoot,
                        "Sample.Holder.KeepAlive",
                        "Sample"))
            ]);
            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 2, maximumInMemoryBytes: long.MaxValue);

            using var handle = await router.RouteAsync(snapshotId, index, CancellationToken.None);
            var indexDirectory = layout.GetHeapIndexDirectory(snapshotId);
            var rootOffsets = new FileInfo(Path.Combine(indexDirectory, "roots-by-object.bin"));

            Assert.AreEqual((3L + 1) * sizeof(long), rootOffsets.Length);
            var path = handle.GetRetentionPaths(3, 1, CancellationToken.None);
            Assert.IsNotNull(path);
            Assert.AreEqual("Sample.Holder.KeepAlive", path.Paths[0].Root.FunctionName);
            CollectionAssert.AreEqual(new ulong[] { 1, 2, 3 }, path.Paths[0].Objects.Select(item => item.Address).ToArray());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 高 fan-in 目标达到访问对象预算时，映射查询必须在登记下一个对象前稳定停止。
    /// </summary>
    [TestMethod]
    public async Task MappedHeapIndex_WhenVisitedObjectBudgetIsReached_ThrowsStableLimitError()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.PathVisitedLimit.{Guid.NewGuid():N}");
        try
        {
            var type = new TypeIdentity("Node", "Sample");
            var index = new SnapshotIndex(
            [
                new SnapshotIndex.ObjectRow(1, type, 16),
                new SnapshotIndex.ObjectRow(2, type, 16),
                new SnapshotIndex.ObjectRow(3, type, 16)
            ],
            new Dictionary<ulong, IReadOnlyList<ulong>> { [2] = [1], [3] = [1] },
            roots: [3]);
            var indexDirectory = await BuildMappedArtifactAsync(root, index);
            using var mapped = new MappedHeapIndex(
                indexDirectory,
                new MappedHeapIndex.RetentionPathQueryLimits(2, 100, 100, TimeSpan.FromMinutes(1)),
                TimeProvider.System);

            var exception = Assert.ThrowsExactly<DiagnosticsException>(
                () => mapped.GetRetentionPaths(1, 1, CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotQueryLimitReached, exception.ErrorCode);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 少量节点也可能拥有稠密入边；扫描边数达到预算后必须停止，而不能只依赖已访问对象数。
    /// </summary>
    [TestMethod]
    public async Task MappedHeapIndex_WhenEdgeScanBudgetIsReached_ThrowsStableLimitError()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.PathEdgeLimit.{Guid.NewGuid():N}");
        try
        {
            var type = new TypeIdentity("Node", "Sample");
            var index = new SnapshotIndex(
            [
                new SnapshotIndex.ObjectRow(1, type, 16),
                new SnapshotIndex.ObjectRow(2, type, 16),
                new SnapshotIndex.ObjectRow(3, type, 16)
            ],
            new Dictionary<ulong, IReadOnlyList<ulong>> { [2] = [1], [3] = [1] },
            roots: [3]);
            var indexDirectory = await BuildMappedArtifactAsync(root, index);
            using var mapped = new MappedHeapIndex(
                indexDirectory,
                new MappedHeapIndex.RetentionPathQueryLimits(100, 1, 100, TimeSpan.FromMinutes(1)),
                TimeProvider.System);

            var exception = Assert.ThrowsExactly<DiagnosticsException>(
                () => mapped.GetRetentionPaths(1, 1, CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotQueryLimitReached, exception.ErrorCode);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 即使节点和边预算尚有余量，队列入队与出队工作量达到上限时也必须稳定终止查询。
    /// </summary>
    [TestMethod]
    public async Task MappedHeapIndex_WhenQueueWorkBudgetIsReached_ThrowsStableLimitError()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.PathQueueLimit.{Guid.NewGuid():N}");
        try
        {
            var type = new TypeIdentity("Node", "Sample");
            var index = new SnapshotIndex(
            [
                new SnapshotIndex.ObjectRow(1, type, 16),
                new SnapshotIndex.ObjectRow(2, type, 16),
                new SnapshotIndex.ObjectRow(3, type, 16)
            ],
            new Dictionary<ulong, IReadOnlyList<ulong>> { [2] = [1], [3] = [2] },
            roots: [3]);
            var indexDirectory = await BuildMappedArtifactAsync(root, index);
            using var mapped = new MappedHeapIndex(
                indexDirectory,
                new MappedHeapIndex.RetentionPathQueryLimits(100, 100, 2, TimeSpan.FromMinutes(1)),
                TimeProvider.System);

            var exception = Assert.ThrowsExactly<DiagnosticsException>(
                () => mapped.GetRetentionPaths(1, 1, CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotQueryLimitReached, exception.ErrorCode);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 可控单调时钟超过截止时间时，路径查询必须返回稳定限制错误且不依赖真实机器调度速度。
    /// </summary>
    [TestMethod]
    public async Task MappedHeapIndex_WhenElapsedTimeBudgetIsReached_ThrowsStableLimitError()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.PathElapsedLimit.{Guid.NewGuid():N}");
        try
        {
            var type = new TypeIdentity("Node", "Sample");
            var index = new SnapshotIndex(
                [new SnapshotIndex.ObjectRow(1, type, 16)],
                roots: [1]);
            var indexDirectory = await BuildMappedArtifactAsync(root, index);
            using var mapped = new MappedHeapIndex(
                indexDirectory,
                new MappedHeapIndex.RetentionPathQueryLimits(100, 100, 100, TimeSpan.FromMilliseconds(1)),
                new AdvancingTimeProvider(TimeSpan.FromMilliseconds(2)));

            var exception = Assert.ThrowsExactly<DiagnosticsException>(
                () => mapped.GetRetentionPaths(1, 1, CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotQueryLimitReached, exception.ErrorCode);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 映射索引的支配树首次查询应生成独立的派生工件，避免每次查询重新把整个对象图装入托管索引。
    /// </summary>
    [TestMethod]
    public async Task HeapIndexRouter_WhenMappedDominatorIsRequested_PublishesDerivedArtifact()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.DominatorRouter.{Guid.NewGuid():N}");
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            var type = new TypeIdentity("Node", "Sample");
            var index = new SnapshotIndex(
            [
                new SnapshotIndex.ObjectRow(1, type, 10),
                new SnapshotIndex.ObjectRow(2, type, 20),
                new SnapshotIndex.ObjectRow(3, type, 30)
            ],
            new Dictionary<ulong, IReadOnlyList<ulong>> { [1] = [2], [2] = [3] },
            roots: [1]);
            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 2, maximumInMemoryBytes: long.MaxValue);

            using var handle = await router.RouteAsync(snapshotId, index, CancellationToken.None);
            var page = await handle.GetDominatorPageAsync(0, 10, CancellationToken.None);

            Assert.AreEqual(3L, page.TotalObjectCount);
            Assert.AreEqual(60L, page.Objects.Single(item => item.ObjectInfo.Address == 1).RetainedSizeBytes);
            Assert.IsTrue(File.Exists(Path.Combine(layout.GetHeapIndexDirectory(snapshotId), ".heapderived", "manifest.json")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 映射 CSR 的支配树必须和常驻索引在菱形图上产生相同的直接支配者与 retained size。
    /// </summary>
    [TestMethod]
    public async Task HeapIndexRouter_WhenMappedDominatorContainsDiamond_MatchesInMemoryResult()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.DominatorDiamond.{Guid.NewGuid():N}");
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            var type = new TypeIdentity("Node", "Sample");
            var index = new SnapshotIndex(
            [
                new SnapshotIndex.ObjectRow(1, type, 10),
                new SnapshotIndex.ObjectRow(2, type, 20),
                new SnapshotIndex.ObjectRow(3, type, 30),
                new SnapshotIndex.ObjectRow(4, type, 40)
            ],
            new Dictionary<ulong, IReadOnlyList<ulong>>
            {
                [1] = [2, 3],
                [2] = [4],
                [3] = [4]
            },
            roots: [1]);
            var expected = index.GetDominatorPage(0, 10).Objects
                .OrderBy(item => item.ObjectInfo.Address)
                .ToArray();
            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 2, maximumInMemoryBytes: long.MaxValue);

            using var handle = await router.RouteAsync(snapshotId, index, CancellationToken.None);
            var actual = (await handle.GetDominatorPageAsync(0, 10, CancellationToken.None)).Objects
                .OrderBy(item => item.ObjectInfo.Address)
                .ToArray();

            Assert.HasCount(expected.Length, actual);
            for (var item = 0; item < expected.Length; item++)
            {
                Assert.AreEqual(expected[item].ObjectInfo.Address, actual[item].ObjectInfo.Address);
                Assert.AreEqual(expected[item].ImmediateDominatorAddress, actual[item].ImmediateDominatorAddress);
                Assert.AreEqual(expected[item].RetainedSizeBytes, actual[item].RetainedSizeBytes);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 损坏的派生工件不能影响基础索引，并应被保留后自动重建为新的可验证结果。
    /// </summary>
    [TestMethod]
    public async Task HeapIndexRouter_WhenDerivedArtifactIsCorrupted_ArchivesAndRebuildsIt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.DominatorRecovery.{Guid.NewGuid():N}");
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            var type = new TypeIdentity("Node", "Sample");
            var index = new SnapshotIndex(
            [
                new SnapshotIndex.ObjectRow(1, type, 10),
                new SnapshotIndex.ObjectRow(2, type, 20)
            ],
            new Dictionary<ulong, IReadOnlyList<ulong>> { [1] = [2] },
            roots: [1]);
            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 1, maximumInMemoryBytes: long.MaxValue);
            var heapIndexDirectory = layout.GetHeapIndexDirectory(snapshotId);

            using (var first = await router.RouteAsync(snapshotId, index, CancellationToken.None))
            {
                _ = await first.GetDominatorPageAsync(0, 10, CancellationToken.None);
            }

            await File.WriteAllBytesAsync(Path.Combine(heapIndexDirectory, ".heapderived", "dominator-order.bin"), [0xFF]);

            using var recovered = await router.RouteAsync(snapshotId, index, CancellationToken.None);
            var page = await recovered.GetDominatorPageAsync(0, 10, CancellationToken.None);

            Assert.AreEqual(2L, page.TotalObjectCount);
            Assert.IsTrue(File.Exists(Path.Combine(heapIndexDirectory, ".heapderived", "manifest.json")));
            Assert.IsGreaterThan(0, Directory.GetDirectories(heapIndexDirectory, ".heapderived.corrupt.*").Length);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// v3 基础索引清单即使其中已声明对象文件的长度和摘要都正确，
    /// 只要遗漏分页、CSR 或根证据等必需工件，或包含空工件条目，也必须归档并重建，不能把畸形清单误判为可复用工件。
    /// </summary>
    [TestMethod]
    public async Task HeapIndexRouter_WhenVersion3ManifestOmitsRequiredArtifacts_ArchivesAndRebuildsIt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.TruncatedManifestRecovery.{Guid.NewGuid():N}");
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            var type = new TypeIdentity("Node", "Sample");
            var index = new SnapshotIndex(
            [
                new SnapshotIndex.ObjectRow(1, type, 16),
                new SnapshotIndex.ObjectRow(2, type, 16)
            ],
            new Dictionary<ulong, IReadOnlyList<ulong>> { [1] = [2] },
            roots: [1]);
            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 1, maximumInMemoryBytes: long.MaxValue);
            var indexDirectory = layout.GetHeapIndexDirectory(snapshotId);

            using (var original = await router.RouteAsync(snapshotId, index, CancellationToken.None))
            {
                Assert.IsTrue(original.IsMapped);
            }

            var objectsPath = Path.Combine(indexDirectory, "objects.bin");
            var objectsBytes = await File.ReadAllBytesAsync(objectsPath);
            var truncatedManifest = new
            {
                Version = 3,
                Completed = true,
                Files = new[]
                {
                    new
                    {
                        Name = "objects.bin",
                        Length = new FileInfo(objectsPath).Length,
                        Sha256 = Convert.ToHexString(SHA256.HashData(objectsBytes))
                    }
                }
            };
            await File.WriteAllTextAsync(
                Path.Combine(indexDirectory, "manifest.json"),
                JsonSerializer.Serialize(truncatedManifest));

            string?[] names;
            using (var recovered = await router.RouteAsync(snapshotId, index, CancellationToken.None))
            {
                await using var manifestStream = File.OpenRead(Path.Combine(indexDirectory, "manifest.json"));
                using var manifest = await JsonDocument.ParseAsync(manifestStream);
                names = manifest.RootElement.GetProperty("Files")
                    .EnumerateArray()
                    .Select(file => file.GetProperty("Name").GetString())
                    .ToArray();
            }

            Assert.IsTrue(names.Contains("types.bin", StringComparer.Ordinal));
            Assert.IsTrue(names.Contains("forward-offsets.bin", StringComparer.Ordinal));
            Assert.IsTrue(names.Contains("root-evidence.bin", StringComparer.Ordinal));
            Assert.IsGreaterThan(0, Directory.GetDirectories(layout.GetSnapshotDirectory(snapshotId), ".heapidx.incomplete.*").Length);

            await File.WriteAllTextAsync(
                Path.Combine(indexDirectory, "manifest.json"),
                "{\"Version\":3,\"Completed\":true,\"Files\":[null,null,null,null,null,null,null,null,null,null,null]}");

            using var recoveredMalformed = await router.RouteAsync(snapshotId, index, CancellationToken.None);
            Assert.IsTrue(recoveredMalformed.IsMapped);
            Assert.IsGreaterThanOrEqualTo(2, Directory.GetDirectories(layout.GetSnapshotDirectory(snapshotId), ".heapidx.incomplete.*").Length);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 已重新计算摘要的 v3 工件若破坏任一跨文件语义约束，热打开必须在返回句柄前拒绝，
    /// 随后的正常路由必须归档损坏目录并从可信输入重建。
    /// </summary>
    /// <param name="corruption">要注入且随后重新计算清单摘要的语义损坏类型。</param>
    [TestMethod]
    [DataRow("duplicate-type")]
    [DataRow("type-summary-count")]
    [DataRow("object-type-index")]
    [DataRow("address-object-id")]
    [DataRow("objects-by-type-order")]
    [DataRow("forward-offset")]
    [DataRow("forward-target")]
    [DataRow("reverse-offset")]
    [DataRow("reverse-target")]
    [DataRow("csr-transpose")]
    [DataRow("root-offset")]
    [DataRow("root-evidence")]
    public async Task HeapIndexRouter_WhenSelfHashedVersion3ArtifactIsSemanticallyInvalid_RejectsAndRebuildsBeforeQuery(
        string corruption)
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.SemanticIndexRecovery.{Guid.NewGuid():N}");
        try
        {
            var typeA = new TypeIdentity("Sample.NodeA", "Sample");
            var typeB = new TypeIdentity("Sample.NodeB", "Sample");
            var index = new SnapshotIndex(
            [
                new SnapshotIndex.ObjectRow(10, typeA, 10),
                new SnapshotIndex.ObjectRow(20, typeB, 20),
                new SnapshotIndex.ObjectRow(30, typeA, 30)
            ],
            new Dictionary<ulong, IReadOnlyList<ulong>>
            {
                [10] = [20],
                [20] = [30]
            },
            retentionRoots:
            [
                new SnapshotIndex.RetentionRootRow(
                    10,
                    new MemoryRetentionRoot(
                        MemoryRootKind.Stack,
                        MemoryRootFlags.StackRoot,
                        "Sample.Holder.KeepAlive",
                        "Sample"))
            ]);
            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 0, maximumInMemoryBytes: 0);
            using (var original = await router.RouteAsync(snapshotId, index, CancellationToken.None))
            {
                Assert.IsTrue(original.IsMapped);
            }

            var indexDirectory = layout.GetHeapIndexDirectory(snapshotId);
            var artifactName = await CorruptHeapIndexArtifactAsync(indexDirectory, corruption);
            await RehashHeapIndexArtifactAsync(indexDirectory, artifactName);

            using var hotOpened = await router.TryOpenExistingMappedAsync(snapshotId, CancellationToken.None);
            Assert.IsNull(hotOpened, $"Semantic corruption '{corruption}' was returned as a queryable hot-open handle.");

            using var recovered = await router.RouteAsync(snapshotId, index, CancellationToken.None);
            Assert.AreEqual(3L, recovered.TypeSummaries.Sum(static summary => summary.ObjectCount));
            Assert.IsGreaterThan(
                0,
                Directory.GetDirectories(layout.GetSnapshotDirectory(snapshotId), ".heapidx.incomplete.*").Length,
                $"Semantic corruption '{corruption}' was not archived before rebuild.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 基础索引目录若因中断构建而不完整，必须保留原始快照并归档不完整工件后重建，不能永久阻塞后续分析。
    /// </summary>
    [TestMethod]
    public async Task HeapIndexRouter_WhenBaseArtifactIsIncomplete_ArchivesAndRebuildsIt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.BaseIndexRecovery.{Guid.NewGuid():N}");
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            var indexDirectory = layout.GetHeapIndexDirectory(snapshotId);
            Directory.CreateDirectory(indexDirectory);
            await File.WriteAllTextAsync(Path.Combine(indexDirectory, "partial.bin"), "interrupted");
            var type = new TypeIdentity("Node", "Sample");
            var index = new SnapshotIndex(
            [
                new SnapshotIndex.ObjectRow(1, type, 16),
                new SnapshotIndex.ObjectRow(2, type, 16)
            ],
            new Dictionary<ulong, IReadOnlyList<ulong>> { [1] = [2] },
            roots: [1]);
            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 1, maximumInMemoryBytes: long.MaxValue);

            using var handle = await router.RouteAsync(snapshotId, index, CancellationToken.None);

            Assert.IsTrue(handle.IsMapped);
            Assert.IsTrue(File.Exists(Path.Combine(indexDirectory, "manifest.json")));
            Assert.IsGreaterThan(0, Directory.GetDirectories(layout.GetSnapshotDirectory(snapshotId), ".heapidx.incomplete.*").Length);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 写出包含两个重复类型身份槽位的最小 FastSerialization GCHeapDump 测试夹具。
    /// </summary>
    /// <param name="path">待创建的快照文件路径。</param>
    private static void WriteFastSerializationWithDuplicateTypes(string path)
    {
        var type = new TypeIdentity("Sample.Node", "Sample");
        var objects = new MemoryObjectInfo[]
        {
            new(1, type, 24),
            new(2, type, 32)
        };
        var types = new (string Name, int Size, string? Module)[]
        {
            ("UNDEFINED", 0, null),
            ("[.NET Roots]", 0, null),
            (type.TypeName, 24, type.AssemblyName),
            (type.TypeName, 32, type.AssemblyName)
        };
        var labels = new int[3];
        using var blob = new MemoryStream();
        using (var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WriteFastNode(writer, labels, nodeIndex: 0, typeIndex: 1, [1]);
            WriteFastNode(writer, labels, nodeIndex: 1, typeIndex: 2, [2]);
            WriteFastNode(writer, labels, nodeIndex: 2, typeIndex: 3, []);
        }

        var graph = new MemoryGraph(types, labels, blob.ToArray(), objects, CancellationToken.None);
        var serializer = new Serializer(path, new GCHeapDump(graph), FileShare.Read);
        serializer.Close();
    }

    /// <summary>
    /// 写出仅使用普通类型槽位的 FastSerialization 图，使两个保留类型保持零对象统计。
    /// </summary>
    /// <param name="path">待创建的快照路径。</param>
    private static void WriteFastSerializationWithZeroCountReservedTypes(string path)
    {
        var type = new TypeIdentity("Sample.Node", "Sample");
        var objects = new MemoryObjectInfo[]
        {
            new(1, type, 24),
            new(2, type, 32)
        };
        var types = new (string Name, int Size, string? Module)[]
        {
            ("UNDEFINED", 0, null),
            ("[.NET Roots]", 0, null),
            (type.TypeName, 24, type.AssemblyName),
            (type.TypeName, 32, type.AssemblyName)
        };
        var labels = new int[3];
        using var blob = new MemoryStream();
        using (var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WriteFastNode(writer, labels, nodeIndex: 0, typeIndex: 2, [1]);
            WriteFastNode(writer, labels, nodeIndex: 1, typeIndex: 2, [2]);
            WriteFastNode(writer, labels, nodeIndex: 2, typeIndex: 3, []);
        }

        var graph = new MemoryGraph(types, labels, blob.ToArray(), objects, CancellationToken.None);
        var serializer = new Serializer(path, new GCHeapDump(graph), FileShare.Read);
        serializer.Close();
    }

    /// <summary>
    /// 将给定内存索引强制发布为映射工件，并返回完成校验后的工件目录。
    /// </summary>
    /// <param name="root">测试专用快照根目录。</param>
    /// <param name="index">待发布的内存图。</param>
    /// <returns>已发布的 v3 `.heapidx` 目录。</returns>
    private static async Task<string> BuildMappedArtifactAsync(string root, SnapshotIndex index)
    {
        var layout = new SnapshotStorageLayout(root);
        var snapshotId = MemorySnapshotId.New();
        var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 0, maximumInMemoryBytes: 0);
        using var handle = await router.RouteAsync(snapshotId, index, CancellationToken.None);
        Assert.IsTrue(handle.IsMapped);
        return layout.GetHeapIndexDirectory(snapshotId);
    }

    /// <summary>
    /// 在保持文件长度和基本编码可读的前提下，向一个 v3 工件注入指定语义损坏。
    /// </summary>
    /// <param name="directory">已发布的测试索引目录。</param>
    /// <param name="corruption">损坏场景名称。</param>
    /// <returns>被修改且需要更新清单摘要的工件名。</returns>
    private static async Task<string> CorruptHeapIndexArtifactAsync(string directory, string corruption)
    {
        var artifactName = corruption switch
        {
            "duplicate-type" => "types.bin",
            "type-summary-count" => "type-summary.bin",
            "object-type-index" => "objects.bin",
            "address-object-id" => "address-to-id.bin",
            "objects-by-type-order" => "objects-by-type.bin",
            "forward-offset" => "forward-offsets.bin",
            "forward-target" or "csr-transpose" => "forward-targets.bin",
            "reverse-offset" => "reverse-offsets.bin",
            "reverse-target" => "reverse-targets.bin",
            "root-offset" => "roots-by-object.bin",
            "root-evidence" => "root-evidence.bin",
            _ => throw new ArgumentOutOfRangeException(nameof(corruption), corruption, "Unknown semantic corruption fixture.")
        };
        var path = Path.Combine(directory, artifactName);
        switch (corruption)
        {
            case "duplicate-type":
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    _ = reader.ReadInt32();
                    _ = reader.ReadString();
                    _ = reader.ReadString();
                    var secondName = reader.ReadString();
                    Assert.AreEqual("Sample.NodeB", secondName);
                    stream.Position -= 1;
                    stream.WriteByte((byte)'A');
                }
                break;
            case "type-summary-count":
                var summaries = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsArray();
                summaries[0]!["ObjectCount"] = 999;
                await File.WriteAllTextAsync(path, summaries.ToJsonString());
                break;
            case "object-type-index":
                WriteInt32(path, sizeof(ulong), 99);
                break;
            case "address-object-id":
                WriteInt32(path, sizeof(ulong), 1);
                break;
            case "objects-by-type-order":
                WriteInt32(path, 0, 2);
                WriteInt32(path, sizeof(int), 0);
                break;
            case "forward-offset":
                WriteInt64(path, sizeof(long), -1);
                break;
            case "forward-target":
                WriteInt32(path, 0, 3);
                break;
            case "reverse-offset":
                WriteInt64(path, sizeof(long), -1);
                break;
            case "reverse-target":
                WriteInt32(path, 0, 3);
                break;
            case "csr-transpose":
                WriteInt32(path, 0, 2);
                break;
            case "root-offset":
                WriteInt64(path, sizeof(long), new FileInfo(Path.Combine(directory, "root-evidence.bin")).Length + 1);
                break;
            case "root-evidence":
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    stream.WriteByte(byte.MaxValue);
                }
                break;
        }

        return artifactName;
    }

    /// <summary>
    /// 将固定偏移处的 32 位整数原位替换，保留工件长度以隔离语义校验行为。
    /// </summary>
    private static void WriteInt32(string path, long offset, int value)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        stream.Position = offset;
        writer.Write(value);
    }

    /// <summary>
    /// 将固定偏移处的 64 位整数原位替换，保留工件长度以隔离语义校验行为。
    /// </summary>
    private static void WriteInt64(string path, long offset, long value)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        stream.Position = offset;
        writer.Write(value);
    }

    /// <summary>
    /// 用修改后工件的真实长度和 SHA-256 更新 v3 清单，使测试不能依赖摘要不匹配而通过。
    /// </summary>
    private static async Task RehashHeapIndexArtifactAsync(string directory, string artifactName)
    {
        var artifactPath = Path.Combine(directory, artifactName);
        var manifestPath = Path.Combine(directory, "manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        var entry = manifest["Files"]!.AsArray()
            .Select(static node => node!.AsObject())
            .Single(node => string.Equals((string?)node["Name"], artifactName, StringComparison.Ordinal));
        entry["Length"] = new FileInfo(artifactPath).Length;
        entry["Sha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(artifactPath)));
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
    }

    /// <summary>
    /// 按 MemoryGraph 节点 blob 格式写入类型索引和相对 child 索引。
    /// </summary>
    /// <param name="writer">节点 blob 写入器。</param>
    /// <param name="labels">每个节点在 blob 中的起始位置。</param>
    /// <param name="nodeIndex">当前节点索引。</param>
    /// <param name="typeIndex">当前原始类型槽位索引。</param>
    /// <param name="children">当前节点的 child 节点索引。</param>
    private static void WriteFastNode(BinaryWriter writer, int[] labels, int nodeIndex, int typeIndex, int[] children)
    {
        labels[nodeIndex] = checked((int)writer.BaseStream.Position);
        WriteFastCompressedInt(writer, typeIndex << 1);
        WriteFastCompressedInt(writer, children.Length);
        foreach (var child in children)
        {
            WriteFastCompressedInt(writer, checked(child - nodeIndex));
        }
    }

    /// <summary>
    /// 按 FastSerialization MemoryGraph 使用的七位分组格式写入有符号整数。
    /// </summary>
    /// <param name="writer">目标二进制写入器。</param>
    /// <param name="value">待编码的整数。</param>
    private static void WriteFastCompressedInt(BinaryWriter writer, int value)
    {
        if (value << 25 >> 25 != value)
        {
            if (value << 18 >> 18 != value)
            {
                if (value << 11 >> 11 != value)
                {
                    if (value << 4 >> 4 != value)
                    {
                        writer.Write((byte)((value >> 28) | 0x80));
                    }

                    writer.Write((byte)((value >> 21) | 0x80));
                }

                writer.Write((byte)((value >> 14) | 0x80));
            }

            writer.Write((byte)((value >> 7) | 0x80));
        }

        writer.Write((byte)(value & 0x7F));
    }

    /// <summary>
    /// 仅为测试夹具提供标准 GCHeapDump FastSerialization 包装，使生产读取器走真实外部文件入口。
    /// </summary>
    private sealed class GCHeapDump : IFastSerializable, IFastSerializableVersion
    {
        private readonly MemoryGraph _graph;

        /// <summary>
        /// 创建包装指定 MemoryGraph 的测试快照根对象。
        /// </summary>
        /// <param name="graph">待序列化的内存图。</param>
        public GCHeapDump(MemoryGraph graph) => _graph = graph;

        int IFastSerializableVersion.Version => 10;

        int IFastSerializableVersion.MinimumVersionCanRead => 4;

        int IFastSerializableVersion.MinimumReaderVersion => 8;

        /// <inheritdoc />
        void IFastSerializable.ToStream(Serializer serializer)
        {
            serializer.Write(_graph);
            serializer.Write(_graph.Is64Bit);
            serializer.Write(1f);
            serializer.Write(1f);
            serializer.Write((IFastSerializable?)null);
            serializer.Write((IFastSerializable?)null);
            serializer.Write((string?)null);
            serializer.Write(DateTime.UtcNow.Ticks);
            serializer.Write(Environment.MachineName);
            serializer.Write("sample");
            serializer.Write(1234);
            serializer.Write(0L);
            serializer.Write(0L);
            serializer.Write(0);
            serializer.WriteTagged((IFastSerializable?)null);
            serializer.WriteTagged("DotnetAnalysis.Tests");
        }

        /// <inheritdoc />
        void IFastSerializable.FromStream(Deserializer deserializer) =>
            throw new NotSupportedException("The test fixture is write-only.");
    }

    /// <summary>
    /// 每次时间戳读取都按固定步长前进的测试时钟，用于确定性验证查询截止时间。
    /// </summary>
    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private readonly long _step;
        private long _timestamp;

        /// <summary>
        /// 创建使用指定时间步长的单调测试时钟。
        /// </summary>
        /// <param name="step">每次读取时间戳增加的正时间跨度。</param>
        public AdvancingTimeProvider(TimeSpan step)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, TimeSpan.Zero);
            _step = step.Ticks;
        }

        /// <inheritdoc />
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <inheritdoc />
        public override long GetTimestamp() => Interlocked.Add(ref _timestamp, _step);
    }
}
