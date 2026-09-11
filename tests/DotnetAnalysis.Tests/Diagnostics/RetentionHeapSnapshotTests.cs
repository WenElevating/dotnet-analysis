using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Tests.Diagnostics;

/// <summary>
/// 验证保留分析专用快照文件的格式边界，避免将未完成或受损文件交给索引构建器。
/// </summary>
[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "测试名描述行为。")]
public sealed class RetentionHeapSnapshotTests
{
    /// <summary>
    /// Profiler 原始记录转换失败后，释放 spool 必须将原始文件归档为证据，不能自动删除。
    /// </summary>
    [TestMethod]
    public async Task WriteFromProfilerSpoolAsync_WhenConversionFails_DisposeArchivesRawEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var spool = await RetentionProfilerRawCaptureSpool.CreateAsync(
                root,
                new RetentionProfilerRawCapture([], [], [], [], []),
                CancellationToken.None);
            await File.WriteAllBytesAsync(spool.ObjectPath, [1]);
            var outputPath = Path.Combine(root, "failed.retentionheap.tmp");

            await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                async () => await RetentionHeapSnapshot.WriteFromProfilerSpoolAsync(
                    outputPath,
                    spool,
                    CancellationToken.None));
            spool.Dispose();

            var evidenceDirectories = Directory.GetDirectories(root, ".retention-raw-evidence.*");
            Assert.HasCount(1, evidenceDirectories);
            CollectionAssert.AreEqual(
                new byte[] { 1 },
                await File.ReadAllBytesAsync(Path.Combine(evidenceDirectories[0], "objects.raw.bin")));
            Assert.IsTrue(File.Exists(Path.Combine(evidenceDirectories[0], "edges.raw.bin")));
            Assert.IsTrue(File.Exists(Path.Combine(evidenceDirectories[0], "roots.raw.bin")));
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
    /// 保留分析格式必须作为独立实现存在，不能伪装成 GCDump 文件。
    /// </summary>
    [TestMethod]
    public void RetentionHeapSnapshot_ExistsAsAnIndependentSnapshotFormat()
    {
        var snapshotType = typeof(GCDumpSnapshotReader).Assembly.GetType(
            "DotnetAnalysis.Diagnostics.Windows.RetentionHeapSnapshot");

        Assert.IsNotNull(snapshotType);
    }

    /// <summary>
    /// 索引读取器边界必须交付可查询句柄而非完整 SnapshotIndex，避免大型快照把完整对象图泄漏回分析服务。
    /// </summary>
    [TestMethod]
    public void IndexedSnapshotReader_UsesIndexHandleBuildContract()
    {
        var contract = typeof(RetentionHeapSnapshot).Assembly.GetType(
            "DotnetAnalysis.Diagnostics.Windows.IIndexedMemorySnapshotReader");

        Assert.IsNotNull(contract);
        Assert.IsNull(contract.GetMethod("ReadIndexAsync"));
        Assert.IsNotNull(contract.GetMethod("BuildIndexAsync"));
    }

    /// <summary>
    /// 大型保留快照的计数预读只允许跳过类型表，不能调用会构造完整 TypeIdentity 数组的读取路径。
    /// </summary>
    [TestMethod]
    public void RetentionHeapSnapshot_ExposesStreamingTypeTableSkipForCountPreRead()
    {
        var method = typeof(RetentionHeapSnapshot).GetMethod(
            "SkipTypesAsync",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        Assert.IsNotNull(method);
    }

    /// <summary>
    /// 大型保留快照的固定宽度记录读写必须通过有界缓冲批量进入文件流，
    /// 不能为每个对象或边执行一次 4 至 24 字节的异步文件操作。
    /// </summary>
    [TestMethod]
    public void RetentionHeapSnapshot_UsesBoundedBuffersForPayloadRecordIo()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var writer = typeof(RetentionHeapSnapshot).GetNestedType("PayloadWriter", System.Reflection.BindingFlags.NonPublic);
        var reader = typeof(RetentionHeapSnapshot).GetNestedType("PayloadReader", System.Reflection.BindingFlags.NonPublic);

        Assert.IsNotNull(writer);
        Assert.IsNotNull(reader);
        Assert.IsNotNull(writer.GetField("_writeBuffer", flags));
        Assert.IsNotNull(writer.GetMethod("FlushAsync", flags));
        Assert.IsNotNull(reader.GetField("_readBuffer", flags));
    }

    /// <summary>
    /// 专用格式必须保留栈根 FunctionID 已解析出的函数证据，并可恢复为正常引用路径索引。
    /// </summary>
    [TestMethod]
    public async Task WriteAndReadAsync_PreservesStackRootFunctionEvidenceAndObjectGraph()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.retentionheap");
        try
        {
            await RetentionHeapSnapshot.WriteAsync(
                path,
                new RetentionHeapSnapshot.SnapshotData(
                    [new TypeIdentity("Sample.Node", "Sample")],
                    [
                        new RetentionHeapSnapshot.ObjectRecord(1, 0, 24),
                        new RetentionHeapSnapshot.ObjectRecord(2, 0, 32)
                    ],
                    [new RetentionHeapSnapshot.EdgeRecord(1, 2)],
                    [new RetentionHeapSnapshot.RootRecord(
                        1,
                        new MemoryRetentionRoot(
                            MemoryRootKind.Stack,
                            MemoryRootFlags.StackRoot,
                            "Sample.Holder.KeepAlive",
                            "Sample"))]),
                CancellationToken.None);

            var index = await RetentionHeapSnapshot.ReadIndexAsync(path, CancellationToken.None);
            var paths = index.GetRetentionPaths(2, maxPathCount: 16);

            Assert.IsNotNull(paths);
            Assert.HasCount(1, paths.Paths);
            Assert.AreEqual("Sample.Holder.KeepAlive", paths.Paths[0].Root.FunctionName);
            CollectionAssert.AreEqual(new ulong[] { 1, 2 }, paths.Paths[0].Objects.Select(item => item.Address).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 大型保留快照索引构建必须直接由原始流生成映射工件，且对象分页、引用路径和栈根证据与常驻索引一致。
    /// </summary>
    [TestMethod]
    public async Task WriteMappedArtifactsAsync_StreamsRetentionSnapshotIntoQueryableDiskIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.RetentionMapped.{Guid.NewGuid():N}");
        var snapshotPath = Path.Combine(root, "sample.retentionheap");
        Directory.CreateDirectory(root);
        try
        {
            await RetentionHeapSnapshot.WriteAsync(
                snapshotPath,
                new RetentionHeapSnapshot.SnapshotData(
                    [
                        new TypeIdentity("Sample.Unused", "Sample"),
                        new TypeIdentity("Sample.Node", "Sample")
                    ],
                    [
                        new RetentionHeapSnapshot.ObjectRecord(1, 1, 24),
                        new RetentionHeapSnapshot.ObjectRecord(2, 1, 32),
                        new RetentionHeapSnapshot.ObjectRecord(3, 1, 48)
                    ],
                    [
                        new RetentionHeapSnapshot.EdgeRecord(1, 2),
                        new RetentionHeapSnapshot.EdgeRecord(2, 3)
                    ],
                    [new RetentionHeapSnapshot.RootRecord(
                        1,
                        new MemoryRetentionRoot(
                            MemoryRootKind.Stack,
                            MemoryRootFlags.StackRoot,
                            "Sample.Holder.KeepAlive",
                            "Sample"))]),
                CancellationToken.None);

            var counts = await RetentionHeapSnapshot.ReadCountsAsync(snapshotPath, CancellationToken.None);
            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            var indexDirectory = layout.GetHeapIndexDirectory(snapshotId);
            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 2, maximumInMemoryBytes: long.MaxValue);
            var reader = (IIndexedMemorySnapshotReader)new RetentionHeapSnapshotReader();
            using var handle = await reader.BuildIndexAsync(snapshotId, snapshotPath, router, CancellationToken.None);

            Assert.AreEqual(3, counts.ObjectCount);
            Assert.AreEqual(2, counts.EdgeCount);
            Assert.IsTrue(handle.IsMapped);
            Assert.IsTrue(Directory.Exists(indexDirectory));
            Assert.AreEqual(3L, handle.TypeSummaries.Single().ObjectCount);
            Assert.AreEqual(0L, handle.GetPage(new TypeIdentity("Sample.Unused", "Sample"), 0, 1).TotalObjectCount);
            CollectionAssert.AreEqual(new ulong[] { 2, 3 }, handle.GetPage(new TypeIdentity("Sample.Node", "Sample"), 1, 2).Objects.Select(item => item.Address).ToArray());
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
    /// 不同原始类型索引映射到同一完整类型身份时，磁盘工件必须规范化类型索引并合并统计与分页对象。
    /// </summary>
    [TestMethod]
    public async Task WriteMappedArtifactsAsync_WhenRawTypesShareIdentity_MergesCanonicalType()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.RetentionCanonicalType.{Guid.NewGuid():N}");
        var snapshotPath = Path.Combine(root, "sample.retentionheap");
        Directory.CreateDirectory(root);
        try
        {
            var type = new TypeIdentity("Sample.Node", "Sample");
            await RetentionHeapSnapshot.WriteAsync(
                snapshotPath,
                new RetentionHeapSnapshot.SnapshotData(
                    [type, new TypeIdentity("Sample.Node", "Sample")],
                    [
                        new RetentionHeapSnapshot.ObjectRecord(1, 0, 24),
                        new RetentionHeapSnapshot.ObjectRecord(2, 1, 32)
                    ],
                    [new RetentionHeapSnapshot.EdgeRecord(1, 2)],
                    [new RetentionHeapSnapshot.RootRecord(
                        1,
                        new MemoryRetentionRoot(MemoryRootKind.Unknown, MemoryRootFlags.None, null, null))]),
                CancellationToken.None);

            var layout = new SnapshotStorageLayout(root);
            var reader = (IIndexedMemorySnapshotReader)new RetentionHeapSnapshotReader();
            using var handle = await reader.BuildIndexAsync(
                MemorySnapshotId.New(),
                snapshotPath,
                new HeapIndexRouter(layout, maximumInMemoryObjectCount: 1, maximumInMemoryBytes: long.MaxValue),
                CancellationToken.None);

            var summary = handle.TypeSummaries.Single();
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
    /// 磁盘索引的地址表必须保持一对一；重复对象地址不能被外排排序后静默合并或绑定任意对象 ID。
    /// </summary>
    [TestMethod]
    public async Task WriteMappedArtifactsAsync_WhenObjectAddressesAreDuplicated_ThrowsStableIndexBuildFailure()
    {
        await AssertMappedGraphBuildFailsAsync(
            [
                new RetentionHeapSnapshot.ObjectRecord(1, 0, 24),
                new RetentionHeapSnapshot.ObjectRecord(1, 0, 32)
            ],
            []);
    }

    /// <summary>
    /// 磁盘索引不得静默丢弃未知非零源地址的引用边，行为必须与常驻索引一致。
    /// </summary>
    [TestMethod]
    public async Task WriteMappedArtifactsAsync_WhenEdgeSourceIsDangling_ThrowsStableIndexBuildFailure()
    {
        await AssertMappedGraphBuildFailsAsync(
            [
                new RetentionHeapSnapshot.ObjectRecord(1, 0, 24),
                new RetentionHeapSnapshot.ObjectRecord(2, 0, 32)
            ],
            [new RetentionHeapSnapshot.EdgeRecord(99, 1)]);
    }

    /// <summary>
    /// 磁盘索引不得静默丢弃未知非零目标地址的引用边，行为必须与常驻索引一致。
    /// </summary>
    [TestMethod]
    public async Task WriteMappedArtifactsAsync_WhenEdgeTargetIsDangling_ThrowsStableIndexBuildFailure()
    {
        await AssertMappedGraphBuildFailsAsync(
            [
                new RetentionHeapSnapshot.ObjectRecord(1, 0, 24),
                new RetentionHeapSnapshot.ObjectRecord(2, 0, 32)
            ],
            [new RetentionHeapSnapshot.EdgeRecord(1, 99)]);
    }

    /// <summary>
    /// 已发布索引热重开必须先于原始保留快照扫描；原始文件暂时不可用时仍应复用完整工件。
    /// </summary>
    [TestMethod]
    public async Task BuildIndexAsync_WhenMappedArtifactExistsAndSourceIsUnavailable_ReusesArtifact()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.RetentionHotReopen.{Guid.NewGuid():N}");
        var snapshotPath = Path.Combine(root, "sample.retentionheap");
        Directory.CreateDirectory(root);
        try
        {
            var type = new TypeIdentity("Sample.Node", "Sample");
            await RetentionHeapSnapshot.WriteAsync(
                snapshotPath,
                new RetentionHeapSnapshot.SnapshotData(
                    [type],
                    [
                        new RetentionHeapSnapshot.ObjectRecord(1, 0, 24),
                        new RetentionHeapSnapshot.ObjectRecord(2, 0, 32)
                    ],
                    [new RetentionHeapSnapshot.EdgeRecord(1, 2)],
                    [new RetentionHeapSnapshot.RootRecord(
                        1,
                        new MemoryRetentionRoot(MemoryRootKind.Unknown, MemoryRootFlags.None, null, null))]),
                CancellationToken.None);
            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 1, maximumInMemoryBytes: long.MaxValue);
            var reader = (IIndexedMemorySnapshotReader)new RetentionHeapSnapshotReader();
            using (var built = await reader.BuildIndexAsync(snapshotId, snapshotPath, router, CancellationToken.None))
            {
                Assert.IsTrue(built.IsMapped);
            }

            File.Delete(snapshotPath);

            using var reopened = await reader.BuildIndexAsync(snapshotId, snapshotPath, router, CancellationToken.None);

            Assert.IsTrue(reopened.IsMapped);
            Assert.AreEqual(2L, reopened.TypeSummaries.Single().ObjectCount);
            CollectionAssert.AreEqual(
                new ulong[] { 1, 2 },
                reopened.GetPage(type, 0, 2).Objects.Select(item => item.Address).ToArray());
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
    /// 映射索引必须与常驻索引一样合并同一对象上的重复根证据，
    /// 不能让十六条相同高优先级根占满路径上限而隐藏其他真实保留路径。
    /// </summary>
    [TestMethod]
    public async Task WriteMappedArtifactsAsync_WhenDuplicateRootsReachPathLimit_PreservesDistinctRootPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.RetentionMappedRoots.{Guid.NewGuid():N}");
        var snapshotPath = Path.Combine(root, "sample.retentionheap");
        Directory.CreateDirectory(root);
        try
        {
            var duplicateRoot = new MemoryRetentionRoot(
                MemoryRootKind.Stack,
                MemoryRootFlags.StackRoot,
                "Sample.Holder.Duplicate",
                "Sample");
            var distinctRoot = new MemoryRetentionRoot(
                MemoryRootKind.Stack,
                MemoryRootFlags.StackRoot,
                "Sample.Holder.Distinct",
                "Sample");
            var roots = Enumerable.Range(0, 16)
                .Select(_ => new RetentionHeapSnapshot.RootRecord(1, duplicateRoot))
                .Append(new RetentionHeapSnapshot.RootRecord(1, distinctRoot))
                .ToArray();
            await RetentionHeapSnapshot.WriteAsync(
                snapshotPath,
                new RetentionHeapSnapshot.SnapshotData(
                    [new TypeIdentity("Sample.Node", "Sample")],
                    [
                        new RetentionHeapSnapshot.ObjectRecord(1, 0, 24),
                        new RetentionHeapSnapshot.ObjectRecord(2, 0, 32)
                    ],
                    [new RetentionHeapSnapshot.EdgeRecord(1, 2)],
                    roots),
                CancellationToken.None);

            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            var reader = (IIndexedMemorySnapshotReader)new RetentionHeapSnapshotReader();
            using var handle = await reader.BuildIndexAsync(
                snapshotId,
                snapshotPath,
                new HeapIndexRouter(layout, maximumInMemoryObjectCount: 1, maximumInMemoryBytes: long.MaxValue),
                CancellationToken.None);

            var paths = handle.GetRetentionPaths(2, 16, CancellationToken.None);

            Assert.IsNotNull(paths);
            Assert.HasCount(2, paths.Paths);
            var functionNames = paths.Paths.Select(path => path.Root.FunctionName).ToHashSet(StringComparer.Ordinal);
            Assert.IsTrue(functionNames.SetEquals(["Sample.Holder.Duplicate", "Sample.Holder.Distinct"]));
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
    /// Profiler 的大图原始记录必须先落入受控磁盘 spool，再流式写入保留快照；
    /// 该路径不能要求 Controller 把对象、边和根重新组装为全量托管集合。
    /// </summary>
    [TestMethod]
    public async Task WriteFromProfilerSpoolAsync_PreservesGraphAndStackEvidenceWithoutRawGraphLists()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.RetentionSpool.{Guid.NewGuid():N}");
        var path = Path.Combine(root, "sample.retentionheap");
        Directory.CreateDirectory(root);
        try
        {
            using var spool = await RetentionProfilerRawCaptureSpool.CreateAsync(
                root,
                new RetentionProfilerRawCapture(
                    [
                        new RetentionProfilerRawObject((nuint)1, (nuint)10, (nuint)24),
                        new RetentionProfilerRawObject((nuint)2, (nuint)10, (nuint)32)
                    ],
                    [new RetentionProfilerRawEdge((nuint)1, (nuint)2)],
                    [new RetentionProfilerRawRoot((nuint)1, 1, 0, (nuint)100, 0)],
                    [new RetentionProfilerRawFunction((nuint)100, "Sample.Holder.KeepAlive", "Sample")],
                    [new RetentionProfilerRawType((nuint)10, "Sample.Node", "Sample")]),
                CancellationToken.None);

            await RetentionHeapSnapshot.WriteFromProfilerSpoolAsync(path, spool, CancellationToken.None);

            var index = await RetentionHeapSnapshot.ReadIndexAsync(path, CancellationToken.None);
            Assert.AreEqual(2L, index.TypeSummaries.Single().ObjectCount);
            var paths = index.GetRetentionPaths(2, 1);
            Assert.IsNotNull(paths);
            Assert.AreEqual("Sample.Holder.KeepAlive", paths.Paths[0].Root.FunctionName);
            CollectionAssert.AreEqual(new ulong[] { 1, 2 }, paths.Paths[0].Objects.Select(item => item.Address).ToArray());
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
    /// Profiler 原始 spool 中重复对象记录必须在持久化前去重，避免后续索引拒绝重复地址。
    /// </summary>
    [TestMethod]
    public async Task WriteFromProfilerSpoolAsync_WhenObjectRecordsDuplicate_WritesUsableSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.RetentionDuplicateObjects.{Guid.NewGuid():N}");
        var path = Path.Combine(root, "sample.retentionheap");
        Directory.CreateDirectory(root);
        try
        {
            using var spool = await RetentionProfilerRawCaptureSpool.CreateAsync(
                root,
                new RetentionProfilerRawCapture(
                    [
                        new RetentionProfilerRawObject((nuint)1, (nuint)10, (nuint)24),
                        new RetentionProfilerRawObject((nuint)1, (nuint)10, (nuint)24),
                        new RetentionProfilerRawObject((nuint)2, (nuint)10, (nuint)32)
                    ],
                    [], [], [],
                    [new RetentionProfilerRawType((nuint)10, "Sample.Node", "Sample")]),
                CancellationToken.None);

            await RetentionHeapSnapshot.WriteFromProfilerSpoolAsync(path, spool, CancellationToken.None);

            var index = await RetentionHeapSnapshot.ReadIndexAsync(path, CancellationToken.None);
            Assert.AreEqual(2L, index.TypeSummaries.Single().ObjectCount);
            CollectionAssert.AreEqual(new ulong[] { 1, 2 }, index.GetObjects(new TypeIdentity("Sample.Node", "Sample")).Select(item => item.Address).ToArray());
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
    /// 写入给定原始对象图并强制走映射索引，断言完整性缺陷稳定映射为索引构建失败。
    /// </summary>
    /// <param name="objects">待写入的对象记录。</param>
    /// <param name="edges">待写入的地址引用边。</param>
    private static async Task AssertMappedGraphBuildFailsAsync(
        IReadOnlyList<RetentionHeapSnapshot.ObjectRecord> objects,
        IReadOnlyList<RetentionHeapSnapshot.EdgeRecord> edges)
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.RetentionMalformedMapped.{Guid.NewGuid():N}");
        var snapshotPath = Path.Combine(root, "malformed.retentionheap");
        Directory.CreateDirectory(root);
        try
        {
            await RetentionHeapSnapshot.WriteAsync(
                snapshotPath,
                new RetentionHeapSnapshot.SnapshotData(
                    [new TypeIdentity("Sample.Node", "Sample")],
                    objects,
                    edges,
                    []),
                CancellationToken.None);
            var reader = (IIndexedMemorySnapshotReader)new RetentionHeapSnapshotReader();

            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                async () =>
                {
                    using var unused = await reader.BuildIndexAsync(
                        MemorySnapshotId.New(),
                        snapshotPath,
                        new HeapIndexRouter(new SnapshotStorageLayout(root), maximumInMemoryObjectCount: 1, maximumInMemoryBytes: long.MaxValue),
                        CancellationToken.None);
                });

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotIndexBuildFailed, exception.ErrorCode);
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
    /// 修改已经完成的负载必须在分配对象表之前被校验和检测拒绝。
    /// </summary>
    [TestMethod]
    public async Task ValidateAsync_WhenPayloadIsTampered_RejectsTheSnapshotWithoutBuildingAnIndex()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.retentionheap");
        try
        {
            await RetentionHeapSnapshot.WriteAsync(
                path,
                new RetentionHeapSnapshot.SnapshotData(
                    [new TypeIdentity("Sample.Node", "Sample")],
                    [new RetentionHeapSnapshot.ObjectRecord(1, 0, 24)],
                    [],
                    []),
                CancellationToken.None);
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Position = stream.Length - 1;
                var value = stream.ReadByte();
                stream.Position--;
                stream.WriteByte((byte)(value ^ 0xff));
            }

            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                async () => await RetentionHeapSnapshot.ValidateAsync(path, CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.ProfilerCaptureFailed, exception.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 包络长度和校验和均有效但对象记录结构损坏时，结构校验仍必须拒绝快照。
    /// </summary>
    [TestMethod]
    public async Task ValidateAsync_WhenObjectRecordIsMalformedButChecksumIsValid_RejectsTheSnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.retentionheap");
        try
        {
            await RetentionHeapSnapshot.WriteAsync(
                path,
                new RetentionHeapSnapshot.SnapshotData(
                    [new TypeIdentity("Sample.Node", "Sample")],
                    [new RetentionHeapSnapshot.ObjectRecord(1, 0, 24)],
                    [], []),
                CancellationToken.None);

            var bytes = await File.ReadAllBytesAsync(path);
            using (var stream = new MemoryStream(bytes, writable: true))
            using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                stream.Position = 64;
                var typeCount = reader.ReadInt32();
                for (var index = 0; index < typeCount; index++)
                {
                    stream.Position += sizeof(int) + reader.ReadInt32();
                    stream.Position += sizeof(int) + reader.ReadInt32();
                }

                _ = reader.ReadInt32();
                stream.Position += sizeof(ulong);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan((int)stream.Position), 99);
            }

            var checksum = SHA256.HashData(bytes.AsSpan(64));
            checksum.CopyTo(bytes.AsSpan(24, checksum.Length));
            await File.WriteAllBytesAsync(path, bytes);

            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                async () => await RetentionHeapSnapshot.ValidateAsync(path, CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.ProfilerCaptureFailed, exception.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
