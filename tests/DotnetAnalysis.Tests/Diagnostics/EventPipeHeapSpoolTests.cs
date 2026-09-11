using System.Collections;
using System.Reflection;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Tests.Diagnostics;

/// <summary>
/// 验证 EventPipe 原始堆事件先落固定宽度 spool，再按实际规模建立索引的行为边界。
/// </summary>
[TestClass]
public sealed class EventPipeHeapSpoolTests
{
    private static readonly string[] s_expectedManagedCollectionFields = ["_types"];

    /// <summary>
    /// 节点早于类型元数据到达时，小图索引仍须使用最终类型，并保持对象、边和未知根语义。
    /// </summary>
    [TestMethod]
    public async Task BuildInMemoryIndexPreservesDelayedTypeGraphAndUnknownRoot()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var spool = await EventPipeHeapSpool.CreateAsync(root, CancellationToken.None);
            spool.RecordNode(0x10, 7, 16, 1);
            spool.RecordNode(0x20, 7, 24, 0);
            spool.RecordEdgeTarget(0x20);
            spool.RecordRoot(0x10);
            spool.RecordType(7, "Sample.Node");

            var index = spool.BuildInMemoryIndex(CancellationToken.None);

            var type = new TypeIdentity("Sample.Node", null);
            Assert.AreEqual(2L, index.TypeSummaries.Single().ObjectCount);
            CollectionAssert.AreEqual(
                new ulong[] { 0x10, 0x20 },
                index.GetReferencePath(0x20)!.Objects.Select(static item => item.Address).ToArray());
            Assert.AreEqual(24L, index.GetObjects(type).Single(static item => item.Address == 0x20).SizeBytes);
            var rootEvidence = index.GetRetentionPaths(0x20, 1, CancellationToken.None)!.Paths.Single().Root;
            Assert.AreEqual(MemoryRootKind.Unknown, rootEvidence.Kind);
            Assert.IsNull(rootEvidence.FunctionName);
            Assert.IsNull(rootEvidence.ModuleName);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// EventPipe spool 只能在类型数量级保留托管集合；对象、边和根不得再次落入 List 或 Dictionary。
    /// </summary>
    [TestMethod]
    public void TypeShapeDoesNotRetainManagedObjectEdgeOrRootCollections()
    {
        var collectionFields = typeof(EventPipeHeapSpool)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(static field => typeof(IEnumerable).IsAssignableFrom(field.FieldType)
                && field.FieldType != typeof(string))
            .Select(static field => field.Name)
            .ToArray();

        CollectionAssert.AreEquivalent(s_expectedManagedCollectionFields, collectionFields);
    }

    /// <summary>
    /// GCDump 读取器不得保留会把非 FastSerialization EventPipe 图整体物化为 SnapshotIndex 的旧异步入口。
    /// </summary>
    [TestMethod]
    public void GCDumpReaderDoesNotExposeLegacyWholeGraphIndexLoader()
    {
        var method = typeof(GCDumpSnapshotReader).GetMethod(
            "ReadIndexAsync",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.IsNull(method);
    }

    /// <summary>
    /// 创建第三个固定宽度文件前若任一文件打开失败，已经打开的句柄必须关闭，且调用专属目录不得残留。
    /// </summary>
    [TestMethod]
    public void ConstructorFailureClosesCreatedFilesAndRemovesWorkingDirectory()
    {
        var root = CreateTemporaryDirectory();
        var workingDirectory = Path.Combine(root, "spool");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(Path.Combine(workingDirectory, "edge-targets.raw.bin"));
        try
        {
            var constructor = typeof(EventPipeHeapSpool).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [typeof(string), typeof(CancellationToken)],
                modifiers: null)
                ?? throw new InvalidOperationException("未找到 EventPipe spool 私有构造函数。");

            var exception = Assert.ThrowsExactly<TargetInvocationException>(
                () => constructor.Invoke([workingDirectory, CancellationToken.None]));

            Assert.IsTrue(exception.InnerException is IOException or UnauthorizedAccessException);
            Assert.IsFalse(Directory.Exists(workingDirectory));
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// 临时目录被外部句柄短暂占用时，释放仍须关闭 spool 写端且不能用清理异常覆盖主操作结果。
    /// </summary>
    [TestMethod]
    public async Task DisposeDoesNotThrowWhenTemporaryDirectoryIsTemporarilyLocked()
    {
        var root = CreateTemporaryDirectory();
        EventPipeHeapSpool? spool = null;
        FileStream? blocker = null;
        try
        {
            spool = await EventPipeHeapSpool.CreateAsync(root, CancellationToken.None);
            blocker = new FileStream(
                Path.Combine(spool.WorkingDirectory, "cleanup-blocker.bin"),
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None);

            spool.Dispose();

            Assert.IsTrue(Directory.Exists(spool.WorkingDirectory));
        }
        finally
        {
            blocker?.Dispose();
            spool?.Dispose();
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// 大图写入器必须直接从固定宽度 spool 发布全部 v3 工件，并支持分页、反向路径和未知根查询。
    /// </summary>
    [TestMethod]
    public async Task WriteMappedArtifactsSupportsPagingPathAndUnknownRoot()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            using var spool = await EventPipeHeapSpool.CreateAsync(
                layout.GetSnapshotDirectory(snapshotId),
                CancellationToken.None);
            spool.RecordType(2, "Sample.Other");
            spool.RecordNode(0x30, 2, 40, 0);
            spool.RecordNode(0x10, 1, 16, 1);
            spool.RecordNode(0x20, 1, 24, 1);
            spool.RecordEdgeTarget(0x20);
            spool.RecordEdgeTarget(0x30);
            spool.RecordRoot(0x10);

            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 1, maximumInMemoryBytes: 1);
            using var handle = await router.BuildMappedAsync(
                snapshotId,
                (directory, token) => HeapIndexArtifactStore.PublishAsync(
                    directory,
                    spool.WriteMappedArtifactsAsync,
                    HeapArtifactStorageGuard.EstimateIndexBuildPeakBytes(spool.ObjectCount, spool.EdgeCount, 0),
                    token),
                CancellationToken.None);

            Assert.IsTrue(handle.IsMapped);
            Assert.IsFalse(handle.IsInMemoryGraphResident);
            Assert.IsTrue(File.Exists(Path.Combine(layout.GetHeapIndexDirectory(snapshotId), "manifest.json")));
            var unknownType = new TypeIdentity("Type(0x1)", null);
            Assert.AreEqual(2L, handle.TypeSummaries.Single(item => item.Type == unknownType).ObjectCount);
            Assert.AreEqual(0x20UL, handle.GetPage(unknownType, 1, 1).Objects.Single().Address);
            var path = handle.GetRetentionPaths(0x30, 1, CancellationToken.None)!.Paths.Single();
            CollectionAssert.AreEqual(
                new ulong[] { 0x10, 0x20, 0x30 },
                path.Objects.Select(static item => item.Address).ToArray());
            Assert.AreEqual(MemoryRootKind.Unknown, path.Root.Kind);
            Assert.IsNull(path.Root.FunctionName);
            Assert.IsNull(path.Root.ModuleName);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// 固定宽度对象 spool 被截断后不得生成部分成功索引，错误应稳定映射为索引构建失败。
    /// </summary>
    [TestMethod]
    public async Task WriteMappedArtifactsRejectsTruncatedSpool()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var spool = await EventPipeHeapSpool.CreateAsync(root, CancellationToken.None);
            spool.RecordNode(0x10, 1, 16, 0);
            _ = spool.BuildInMemoryIndex(CancellationToken.None);
            var objectPath = Directory.GetFiles(spool.WorkingDirectory, "objects.raw.bin").Single();
            await File.AppendAllBytesAsync(objectPath, [0xFF]);
            var output = Path.Combine(root, "mapped-output");
            Directory.CreateDirectory(output);

            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                () => spool.WriteMappedArtifactsAsync(output, CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotIndexBuildFailed, exception.ErrorCode);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// 对象 spool 即使长度保持不变，只要浅表大小或边槽位被篡改为负值，也不得发布自洽但错误的工件。
    /// </summary>
    [TestMethod]
    public async Task WriteMappedArtifactsRejectsSemanticallyInvalidObjectRecord()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var spool = await EventPipeHeapSpool.CreateAsync(root, CancellationToken.None);
            spool.RecordNode(0x10, 1, 16, 0);
            _ = spool.BuildInMemoryIndex(CancellationToken.None);
            var objectPath = Directory.GetFiles(spool.WorkingDirectory, "objects.raw.bin").Single();
            await using (var stream = new FileStream(objectPath, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                stream.Position = sizeof(ulong) * 2 + sizeof(long);
                await stream.WriteAsync(BitConverter.GetBytes(-1L));
            }

            var output = Path.Combine(root, "mapped-output");
            Directory.CreateDirectory(output);

            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                () => spool.WriteMappedArtifactsAsync(output, CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotIndexBuildFailed, exception.ErrorCode);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// 磁盘索引的地址表必须是一对一映射；重复对象地址不得被静默绑定到任意 objectId。
    /// </summary>
    [TestMethod]
    public async Task WriteMappedArtifactsRejectsDuplicateObjectAddresses()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var spool = await EventPipeHeapSpool.CreateAsync(root, CancellationToken.None);
            spool.RecordNode(0x10, 1, 16, 0);
            spool.RecordNode(0x10, 1, 24, 0);
            var output = Path.Combine(root, "mapped-output");
            Directory.CreateDirectory(output);

            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                () => spool.WriteMappedArtifactsAsync(output, CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotIndexBuildFailed, exception.ErrorCode);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// 磁盘索引地址表不得发布零地址对象；零地址会破坏映射查询的对象语义。
    /// </summary>
    [TestMethod]
    public async Task WriteMappedArtifactsRejectsZeroObjectAddress()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var spool = await EventPipeHeapSpool.CreateAsync(root, CancellationToken.None);
            spool.RecordNode(0, 1, 16, 0);
            var output = Path.Combine(root, "mapped-output");
            Directory.CreateDirectory(output);

            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                () => spool.WriteMappedArtifactsAsync(output, CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotIndexBuildFailed, exception.ErrorCode);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// 小图常驻索引同样要求对象地址唯一，不能让首条记录静默遮蔽后续同地址对象。
    /// </summary>
    [TestMethod]
    public async Task BuildInMemoryIndexRejectsDuplicateObjectAddresses()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var spool = await EventPipeHeapSpool.CreateAsync(root, CancellationToken.None);
            spool.RecordNode(0x10, 1, 16, 0);
            spool.RecordNode(0x10, 1, 24, 0);

            var exception = Assert.ThrowsExactly<DiagnosticsException>(
                () => spool.BuildInMemoryIndex(CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotIndexBuildFailed, exception.ErrorCode);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// 小图不得丢弃指向未知非零对象地址的引用边并伪造完整索引。
    /// </summary>
    [TestMethod]
    public async Task BuildInMemoryIndexRejectsDanglingNonZeroEdgeTarget()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var spool = await EventPipeHeapSpool.CreateAsync(root, CancellationToken.None);
            spool.RecordNode(0x10, 1, 16, 1);
            spool.RecordEdgeTarget(0x99);

            var exception = Assert.ThrowsExactly<DiagnosticsException>(
                () => spool.BuildInMemoryIndex(CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotIndexBuildFailed, exception.ErrorCode);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// 大图地址归并找不到非零边目标时必须稳定失败，不能从正反向 CSR 中静默删边。
    /// </summary>
    [TestMethod]
    public async Task WriteMappedArtifactsRejectsDanglingNonZeroEdgeTarget()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var spool = await EventPipeHeapSpool.CreateAsync(root, CancellationToken.None);
            spool.RecordNode(0x10, 1, 16, 1);
            spool.RecordEdgeTarget(0x99);
            var output = Path.Combine(root, "mapped-output");
            Directory.CreateDirectory(output);

            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                () => spool.WriteMappedArtifactsAsync(output, CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotIndexBuildFailed, exception.ErrorCode);
            Assert.IsInstanceOfType<InvalidDataException>(exception.InnerException);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// 节点声明的边槽位与实际目标记录无论少一条或多一条，都不得生成部分成功的内存索引。
    /// </summary>
    /// <param name="writeUnexpectedEdge">为真时写入多余目标；否则保留缺失目标。</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BuildInMemoryIndexRejectsDeclaredEdgeCountMismatch(bool writeUnexpectedEdge)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var spool = await EventPipeHeapSpool.CreateAsync(root, CancellationToken.None);
            spool.RecordNode(0x10, 1, 16, writeUnexpectedEdge ? 0 : 1);
            if (writeUnexpectedEdge)
            {
                spool.RecordEdgeTarget(0x20);
            }

            var exception = Assert.ThrowsExactly<DiagnosticsException>(
                () => spool.BuildInMemoryIndex(CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotIndexBuildFailed, exception.ErrorCode);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// 零地址边槽位和不在对象表中的根可被忽略；同一有效根的重复事件只能生成一条未知根证据。
    /// </summary>
    [TestMethod]
    public async Task WriteMappedArtifactsDropsZeroEdgesAndDanglingRootsAndDeduplicatesRoots()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            using var spool = await EventPipeHeapSpool.CreateAsync(
                layout.GetSnapshotDirectory(snapshotId),
                CancellationToken.None);
            spool.RecordNode(0x10, 1, 16, 2);
            spool.RecordNode(0x20, 1, 24, 0);
            spool.RecordEdgeTarget(0x20);
            spool.RecordEdgeTarget(0);
            for (var index = 0; index < 256; index++)
            {
                spool.RecordRoot(0x10);
                spool.RecordRoot(0x99);
            }

            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 1, maximumInMemoryBytes: 1);
            using var handle = await GCDumpSnapshotReader.RouteEventPipeSpoolAsync(
                snapshotId,
                spool,
                sourceLengthBytes: 0,
                router,
                CancellationToken.None);

            var path = handle.GetRetentionPaths(0x20, 16, CancellationToken.None);
            Assert.IsNotNull(path);
            Assert.HasCount(1, path.Paths);
            Assert.AreEqual(MemoryRootKind.Unknown, path.Paths[0].Root.Kind);
            CollectionAssert.AreEqual(
                new ulong[] { 0x10, 0x20 },
                path.Paths[0].Objects.Select(static item => item.Address).ToArray());
            Assert.AreEqual(
                sizeof(byte) + sizeof(int) + sizeof(int) * 2,
                new FileInfo(Path.Combine(layout.GetHeapIndexDirectory(snapshotId), "root-evidence.bin")).Length);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// 已验证完成的 v3 工件必须在原始文件检查、EventPipe 解析和容量预留之前直接复用。
    /// </summary>
    [TestMethod]
    public async Task BuildIndexReusesCompleteMappedArtifactWhenSourceFileIsUnavailable()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            var type = new TypeIdentity("Sample.Cached", "Sample");
            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 1, maximumInMemoryBytes: 1);
            using (var published = await router.RouteAsync(
                snapshotId,
                new SnapshotIndex(
                    [new SnapshotIndex.ObjectRow(0x10, type, 32)],
                    roots: [0x10]),
                CancellationToken.None))
            {
                Assert.IsTrue(published.IsMapped);
            }

            var reader = (IIndexedMemorySnapshotReader)new GCDumpSnapshotReader();
            using var reopened = await reader.BuildIndexAsync(
                snapshotId,
                Path.Combine(root, "missing-source.gcdump"),
                router,
                CancellationToken.None);

            Assert.IsTrue(reopened.IsMapped);
            Assert.AreEqual(0x10UL, reopened.GetPage(type, 0, 1).Objects.Single().Address);
            Assert.IsEmpty(
                Directory.GetDirectories(layout.GetSnapshotDirectory(snapshotId), ".eventpipe-heap-spool.*.tmp"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// 已发布 v3 工件的摘要不匹配时不得热复用；读取器必须保留原始 FastSerialization 证据，
    /// 归档损坏目录并以同一快照标识发布可查询的新索引。
    /// </summary>
    [TestMethod]
    public async Task BuildIndexRebuildsCorruptMappedArtifactFromFastSerializationSource()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            var oldType = new TypeIdentity("Sample.Old", "Sample");
            var rebuiltType = new TypeIdentity("Sample.Rebuilt", "Sample");
            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 1, maximumInMemoryBytes: 1);
            using (var published = await router.RouteAsync(
                snapshotId,
                new SnapshotIndex(
                    [new SnapshotIndex.ObjectRow(0x10, oldType, 16)],
                    roots: [0x10]),
                CancellationToken.None))
            {
                Assert.IsTrue(published.IsMapped);
            }

            var indexDirectory = layout.GetHeapIndexDirectory(snapshotId);
            var objectPath = Path.Combine(indexDirectory, "objects.bin");
            using (var stream = new FileStream(objectPath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0xFF);
            }

            var sourcePath = Path.Combine(root, "replacement.gcdump");
            GCDumpFastSerializationWriter.Write(
                sourcePath,
                new GCDumpSnapshotReader.HeapData(
                    [],
                    [new MemoryObjectInfo(0x20, rebuiltType, 48)],
                    new Dictionary<ulong, IReadOnlyList<ulong>>(),
                    [0x20]),
                new TargetProcess(4321, DateTimeOffset.UtcNow.AddMinutes(-1), "replacement", null),
                DateTimeOffset.UtcNow,
                CancellationToken.None);

            var reader = (IIndexedMemorySnapshotReader)new GCDumpSnapshotReader();
            using var rebuilt = await reader.BuildIndexAsync(
                snapshotId,
                sourcePath,
                router,
                CancellationToken.None);

            Assert.IsTrue(rebuilt.IsMapped);
            Assert.AreEqual(0x20UL, rebuilt.GetPage(rebuiltType, 0, 1).Objects.Single().Address);
            Assert.IsTrue(File.Exists(sourcePath));
            Assert.HasCount(
                1,
                Directory.GetDirectories(layout.GetSnapshotDirectory(snapshotId), ".heapidx.incomplete.*"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// 非 FastSerialization 损坏输入必须统一转换为 CaptureFailed，不能泄漏 EventPipe 解析器异常类型。
    /// </summary>
    [TestMethod]
    public async Task BuildIndexWrapsMalformedEventPipeInputAsStableDiagnosticsFailure()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "malformed.gcdump");
            await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)0xCC, 4096).ToArray());
            var layout = new SnapshotStorageLayout(Path.Combine(root, "snapshots"));
            var reader = (IIndexedMemorySnapshotReader)new GCDumpSnapshotReader();

            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                () => reader.BuildIndexAsync(
                    MemorySnapshotId.New(),
                    path,
                    new HeapIndexRouter(layout),
                    CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.CaptureFailed, exception.ErrorCode);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// 无根 EventPipe 图仍须发布完整偏移工件，但任何对象都不得获得伪造的引用路径。
    /// </summary>
    [TestMethod]
    public async Task WriteMappedArtifactsSupportsGraphWithoutRoots()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            using var spool = await EventPipeHeapSpool.CreateAsync(
                layout.GetSnapshotDirectory(snapshotId),
                CancellationToken.None);
            spool.RecordNode(0x10, 1, 16, 0);

            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 1, maximumInMemoryBytes: 1);
            using var handle = await GCDumpSnapshotReader.RouteEventPipeSpoolAsync(
                snapshotId,
                spool,
                sourceLengthBytes: 0,
                router,
                CancellationToken.None);

            Assert.IsNull(handle.GetReferencePath(0x10, CancellationToken.None));
            Assert.AreEqual(0, new FileInfo(Path.Combine(layout.GetHeapIndexDirectory(snapshotId), "root-evidence.bin")).Length);
            Assert.AreEqual(
                sizeof(long) * 2,
                new FileInfo(Path.Combine(layout.GetHeapIndexDirectory(snapshotId), "roots-by-object.bin")).Length);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// EventPipe 弱根标志必须写入映射证据，但不能构成保留路径或 Dominator 虚拟根。
    /// </summary>
    [TestMethod]
    public async Task WeakRootIsPreservedButExcludedFromMappedRetentionAndDominatorAnalysis()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            using var spool = await EventPipeHeapSpool.CreateAsync(
                layout.GetSnapshotDirectory(snapshotId),
                CancellationToken.None);
            spool.RecordNode(0x10, 1, 16, 1);
            spool.RecordNode(0x20, 1, 24, 0);
            spool.RecordEdgeTarget(0x20);
            spool.RecordRoot(0x10, MemoryRootFlags.WeakReference);

            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 1, maximumInMemoryBytes: 1);
            using var handle = await GCDumpSnapshotReader.RouteEventPipeSpoolAsync(
                snapshotId,
                spool,
                sourceLengthBytes: 0,
                router,
                CancellationToken.None);

            Assert.IsNull(handle.GetReferencePath(0x20, CancellationToken.None));
            var dominators = await handle.GetDominatorPageAsync(0, 10, CancellationToken.None);
            Assert.AreEqual(0, dominators.TotalObjectCount);
            Assert.IsEmpty(dominators.Objects);
            using var evidence = new BinaryReader(File.OpenRead(
                Path.Combine(layout.GetHeapIndexDirectory(snapshotId), "root-evidence.bin")));
            Assert.AreEqual(MemoryRootKind.Unknown, (MemoryRootKind)evidence.ReadByte());
            Assert.AreEqual(MemoryRootFlags.WeakReference, (MemoryRootFlags)evidence.ReadInt32());
            Assert.AreEqual(-1, evidence.ReadInt32());
            Assert.AreEqual(-1, evidence.ReadInt32());
            Assert.AreEqual(evidence.BaseStream.Length, evidence.BaseStream.Position);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// 小图索引收到弱根时同样不得把目标对象投影为被强根保留。
    /// </summary>
    [TestMethod]
    public async Task WeakRootIsExcludedFromInMemoryRetentionAnalysis()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var spool = await EventPipeHeapSpool.CreateAsync(root, CancellationToken.None);
            spool.RecordNode(0x10, 1, 16, 1);
            spool.RecordNode(0x20, 1, 24, 0);
            spool.RecordEdgeTarget(0x20);
            spool.RecordRoot(0x10, MemoryRootFlags.WeakReference);

            var index = spool.BuildInMemoryIndex(CancellationToken.None);

            Assert.IsNull(index.GetReferencePath(0x20));
            Assert.AreEqual(0, index.GetDominatorPage(0, 10, CancellationToken.None).TotalObjectCount);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// 同一快照目标的并发大图发布必须复用一份完整工件，两个等待者均获得可查询映射句柄。
    /// </summary>
    [TestMethod]
    public async Task ConcurrentMappedRoutesPublishOneCompleteArtifact()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            using var firstSpool = await CreateTwoObjectSpoolAsync(layout.GetSnapshotDirectory(snapshotId));
            using var secondSpool = await CreateTwoObjectSpoolAsync(layout.GetSnapshotDirectory(snapshotId));
            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 1, maximumInMemoryBytes: 1);

            var firstTask = GCDumpSnapshotReader.RouteEventPipeSpoolAsync(
                snapshotId,
                firstSpool,
                sourceLengthBytes: 0,
                router,
                CancellationToken.None);
            var secondTask = GCDumpSnapshotReader.RouteEventPipeSpoolAsync(
                snapshotId,
                secondSpool,
                sourceLengthBytes: 0,
                router,
                CancellationToken.None);
            using var first = await firstTask;
            using var second = await secondTask;

            Assert.IsTrue(first.IsMapped);
            Assert.IsTrue(second.IsMapped);
            Assert.AreEqual(0x20UL, first.GetPage(new TypeIdentity("Sample.Node", null), 1, 1).Objects.Single().Address);
            Assert.AreEqual(0x20UL, second.GetPage(new TypeIdentity("Sample.Node", null), 1, 1).Objects.Single().Address);
            Assert.HasCount(1, Directory.GetDirectories(layout.GetSnapshotDirectory(snapshotId), ".heapidx"));
            Assert.IsEmpty(Directory.GetDirectories(layout.GetSnapshotDirectory(snapshotId), "..heapidx.*.tmp"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// 调用方在派生工件写入前取消时，只返回取消而不把取消包装为索引失败。
    /// </summary>
    [TestMethod]
    public async Task WriteMappedArtifactsPreservesCancellation()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var spool = await EventPipeHeapSpool.CreateAsync(root, CancellationToken.None);
            spool.RecordNode(0x10, 1, 16, 0);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => spool.WriteMappedArtifactsAsync(Path.Combine(root, "mapped-output"), cancellation.Token));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// EventPipe 回退应在事件流结束后使用实际对象与边计数，小图保留既有常驻索引行为。
    /// </summary>
    [TestMethod]
    public async Task RouteEventPipeSpoolKeepsSmallGraphInMemory()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            using var spool = await EventPipeHeapSpool.CreateAsync(
                layout.GetSnapshotDirectory(snapshotId),
                CancellationToken.None);
            spool.RecordNode(0x10, 1, 16, 0);
            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 2, maximumInMemoryBytes: long.MaxValue);

            using var handle = await GCDumpSnapshotReader.RouteEventPipeSpoolAsync(
                snapshotId,
                spool,
                sourceLengthBytes: 128,
                router,
                CancellationToken.None);

            Assert.IsTrue(handle.IsInMemoryGraphResident);
            Assert.IsFalse(handle.IsMapped);
            Assert.AreEqual(0x10UL, handle.GetPage(new TypeIdentity("Type(0x1)", null), 0, 1).Objects.Single().Address);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// EventPipe 回退达到路由阈值时必须直接发布磁盘工件，句柄不得持有临时构造的 SnapshotIndex。
    /// </summary>
    [TestMethod]
    public async Task RouteEventPipeSpoolUsesMappedIndexAtActualCountThreshold()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            using var spool = await EventPipeHeapSpool.CreateAsync(
                layout.GetSnapshotDirectory(snapshotId),
                CancellationToken.None);
            spool.RecordNode(0x10, 1, 16, 1);
            spool.RecordNode(0x20, 1, 24, 0);
            spool.RecordEdgeTarget(0x20);
            spool.RecordRoot(0x10);
            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 2, maximumInMemoryBytes: long.MaxValue);

            using var handle = await GCDumpSnapshotReader.RouteEventPipeSpoolAsync(
                snapshotId,
                spool,
                sourceLengthBytes: 256,
                router,
                CancellationToken.None);

            Assert.IsTrue(handle.IsMapped);
            Assert.IsFalse(handle.IsInMemoryGraphResident);
            CollectionAssert.AreEqual(
                new ulong[] { 0x10, 0x20 },
                handle.GetReferencePath(0x20, CancellationToken.None)!.Objects.Select(static item => item.Address).ToArray());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>
    /// 创建调用专属测试目录，避免并行测试共享 EventPipe spool 文件。
    /// </summary>
    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.EventPipeSpool.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// 创建两个对象和一条有效引用边的独占 spool，供同目标并发发布测试使用。
    /// </summary>
    /// <param name="parentDirectory">快照目录；每次调用仍会创建唯一子目录。</param>
    /// <returns>尚未冻结且由调用方释放的 EventPipe spool。</returns>
    private static async Task<EventPipeHeapSpool> CreateTwoObjectSpoolAsync(string parentDirectory)
    {
        var spool = await EventPipeHeapSpool.CreateAsync(parentDirectory, CancellationToken.None);
        spool.RecordType(1, "Sample.Node");
        spool.RecordNode(0x10, 1, 16, 1);
        spool.RecordNode(0x20, 1, 24, 0);
        spool.RecordEdgeTarget(0x20);
        spool.RecordRoot(0x10);
        return spool;
    }

    /// <summary>
    /// 删除本测试创建的目录；spool 自身只负责其子目录生命周期。
    /// </summary>
    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
