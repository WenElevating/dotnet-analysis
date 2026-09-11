using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Tests.Diagnostics;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class HeapArtifactRecoveryTests
{
    private const int HotReopenObjectCount = 1_000_000;
    private const long MaximumHotReopenManagedAllocationBytes = 96L * 1024;

    /// <summary>
    /// 同一目标的并发发布必须单飞；后进入者在锁内复核完成态并复用结果，不得归档先完成的目录。
    /// </summary>
    [TestMethod]
    public async Task PublishAsync_WhenSameTargetIsConcurrent_ReusesWinnerWithoutArchivingCompletedArtifact()
    {
        var root = CreateTestDirectory("ConcurrentPublish");
        var indexDirectory = Path.Combine(root, ".heapidx");
        var templateDirectory = Path.Combine(root, "template", ".heapidx");
        var firstWriterEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWriterEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var type = new TypeIdentity("Sample.Node", "Sample");
            var templateIndex = new SnapshotIndex(
                [new SnapshotIndex.ObjectRow(1, type, 16)],
                new Dictionary<ulong, IReadOnlyList<ulong>>(),
                roots: [1]);
            await HeapIndexArtifactStore.PublishAsync(
                templateDirectory,
                templateIndex.ExportArtifactData(),
                CancellationToken.None);
            var first = HeapIndexArtifactStore.PublishAsync(
                indexDirectory,
                async (temporaryDirectory, cancellationToken) =>
                {
                    firstWriterEntered.SetResult();
                    await releaseFirstWriter.Task.WaitAsync(cancellationToken);
                    foreach (var name in s_requiredBaseArtifactNames)
                    {
                        File.Copy(
                            Path.Combine(templateDirectory, name),
                            Path.Combine(temporaryDirectory, name));
                    }

                    return s_requiredBaseArtifactNames;
                },
                4096,
                CancellationToken.None);
            await firstWriterEntered.Task;
            var second = HeapIndexArtifactStore.PublishAsync(
                indexDirectory,
                (temporaryDirectory, cancellationToken) =>
                {
                    secondWriterEntered.SetResult();
                    return Task.FromResult<IReadOnlyList<string>>(s_requiredBaseArtifactNames);
                },
                4096,
                CancellationToken.None);

            var prematureWriter = await Task.WhenAny(secondWriterEntered.Task, Task.Delay(200));
            Assert.AreNotSame(secondWriterEntered.Task, prematureWriter);
            releaseFirstWriter.SetResult();
            await Task.WhenAll(first, second);

            Assert.IsFalse(secondWriterEntered.Task.IsCompleted);
            Assert.IsTrue(Directory.Exists(indexDirectory));
            Assert.IsEmpty(Directory.GetDirectories(root, ".heapidx.incomplete.*"));
        }
        finally
        {
            releaseFirstWriter.TrySetResult();
            DeleteTestDirectory(root);
        }
    }

    private const int DerivedStateRecordBytes = sizeof(int) + sizeof(int) + sizeof(long);
    private static readonly string[] s_requiredBaseArtifactNames =
    [
        "types.bin",
        "type-summary.bin",
        "objects.bin",
        "address-to-id.bin",
        "objects-by-type.bin",
        "forward-offsets.bin",
        "forward-targets.bin",
        "reverse-offsets.bin",
        "reverse-targets.bin",
        "roots-by-object.bin",
        "root-evidence.bin"
    ];

    /// <summary>
    /// 首次发布返回不完整工件列表时必须在活动目录出现前失败，不能等到下一次打开才发现并归档。
    /// </summary>
    [TestMethod]
    public async Task PublishAsync_WhenInitialArtifactListIsIncomplete_RejectsBeforePromotion()
    {
        var root = CreateTestDirectory("InitialPublish");
        var indexDirectory = Path.Combine(root, ".heapidx");
        try
        {
            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                async () => await HeapIndexArtifactStore.PublishAsync(
                    indexDirectory,
                    async (temporaryDirectory, cancellationToken) =>
                    {
                        await File.WriteAllBytesAsync(
                            Path.Combine(temporaryDirectory, "objects.bin"),
                            new byte[sizeof(ulong) + sizeof(int) + sizeof(long)],
                            cancellationToken);
                        return ["objects.bin"];
                    },
                    1024,
                    CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotIndexBuildFailed, exception.ErrorCode);
            Assert.IsFalse(Directory.Exists(indexDirectory));
            Assert.IsEmpty(Directory.GetDirectories(root, "*.tmp"));
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    /// <summary>
    /// 基础发布回调返回空列表、重复名、额外名，或精确声明固定集合却未写入其中一个文件时，
    /// 必须分别在生成 manifest 和提升活动目录前失败。
    /// </summary>
    /// <param name="malformation">要注入的回调返回值或磁盘文件异常。</param>
    [TestMethod]
    [DataRow("null-list")]
    [DataRow("duplicate-name")]
    [DataRow("unexpected-name")]
    [DataRow("missing-file")]
    public async Task PublishAsync_WhenCallbackArtifactListIsMalformed_RejectsBeforePromotion(string malformation)
    {
        var root = CreateTestDirectory($"BaseCallback.{malformation}");
        var indexDirectory = Path.Combine(root, ".heapidx");
        try
        {
            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                async () => await HeapIndexArtifactStore.PublishAsync(
                    indexDirectory,
                    async (temporaryDirectory, cancellationToken) =>
                    {
                        IReadOnlyList<string>? paths = malformation switch
                        {
                            "null-list" => null,
                            "duplicate-name" => [.. s_requiredBaseArtifactNames[..^1], "types.bin"],
                            "unexpected-name" => [.. s_requiredBaseArtifactNames[..^1], "unexpected.bin"],
                            "missing-file" => s_requiredBaseArtifactNames,
                            _ => throw new ArgumentOutOfRangeException(nameof(malformation), malformation, "未知基础工件列表畸形类型。")
                        };
                        if (paths is not null)
                        {
                            foreach (var path in paths.Distinct(StringComparer.Ordinal))
                            {
                                if (malformation == "missing-file" && path == "root-evidence.bin")
                                {
                                    continue;
                                }

                                await File.WriteAllBytesAsync(
                                    Path.Combine(temporaryDirectory, path),
                                    [],
                                    cancellationToken);
                            }
                        }

                        return paths!;
                    },
                    1024,
                    CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotIndexBuildFailed, exception.ErrorCode);
            Assert.IsInstanceOfType<InvalidDataException>(exception.InnerException);
            Assert.IsFalse(Directory.Exists(indexDirectory));
            Assert.IsEmpty(Directory.GetDirectories(root, "*.tmp"));
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    /// <summary>
    /// 清单文件集合中的空、重复、额外、缺失和越界路径条目均不可复用，且原损坏目录必须作为证据保留。
    /// </summary>
    /// <param name="malformation">要注入的清单畸形类型。</param>
    [TestMethod]
    [DataRow("null-list")]
    [DataRow("null-entry")]
    [DataRow("duplicate")]
    [DataRow("unexpected")]
    [DataRow("missing")]
    [DataRow("traversal")]
    public async Task OpenOrBuild_WhenManifestEntriesAreInvalid_ArchivesAndRebuilds(string malformation)
    {
        var root = CreateTestDirectory($"DerivedManifest.{malformation}");
        try
        {
            var heapIndexDirectory = await CreatePublishedDerivedArtifactAsync(root);
            var derivedDirectory = Path.Combine(heapIndexDirectory, ".heapderived");
            var manifestPath = Path.Combine(derivedDirectory, "manifest.json");
            var manifest = await ReadJsonObjectAsync(manifestPath);
            CorruptManifestEntries(manifest, malformation, heapIndexDirectory, derivedDirectory);
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
            var corruptedManifestHash = ComputeHash(manifestPath);
            var baseManifestPath = Path.Combine(heapIndexDirectory, "manifest.json");
            var baseManifestHash = ComputeHash(baseManifestPath);

            var recovered = HeapDerivedAnalysisArtifact.OpenOrBuild(heapIndexDirectory, CancellationToken.None);

            Assert.AreEqual(2, recovered.ObjectCount);
            Assert.HasCount(2, recovered.ReadPage(0, 10, CancellationToken.None));
            var archives = Directory.GetDirectories(heapIndexDirectory, ".heapderived.corrupt.*");
            Assert.HasCount(1, archives);
            Assert.AreEqual(corruptedManifestHash, ComputeHash(Path.Combine(archives[0], "manifest.json")));
            Assert.AreEqual(baseManifestHash, ComputeHash(baseManifestPath));
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    /// <summary>
    /// 状态文件即使摘要同步更新，只要记录数不等于基础对象数，也必须归档后重建。
    /// </summary>
    [TestMethod]
    public async Task OpenOrBuild_WhenStateLengthDoesNotMatchBaseObjectCount_ArchivesAndRebuilds()
    {
        var root = CreateTestDirectory("DerivedStateLength");
        try
        {
            var heapIndexDirectory = await CreatePublishedDerivedArtifactAsync(root);
            var derivedDirectory = Path.Combine(heapIndexDirectory, ".heapderived");
            var statePath = Path.Combine(derivedDirectory, "dominator-state.bin");
            await using (var stream = new FileStream(statePath, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(new byte[DerivedStateRecordBytes]);
            }

            await RefreshManifestFileAsync(derivedDirectory, "dominator-state.bin");

            var recovered = HeapDerivedAnalysisArtifact.OpenOrBuild(heapIndexDirectory, CancellationToken.None);

            Assert.AreEqual(2, recovered.ObjectCount);
            Assert.AreEqual(2L * DerivedStateRecordBytes, new FileInfo(statePath).Length);
            Assert.HasCount(1, Directory.GetDirectories(heapIndexDirectory, ".heapderived.corrupt.*"));
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    /// <summary>
    /// 清单声明的可达对象数不能超过基础 objects.bin 的对象总数，即使 order 长度和摘要与伪造声明一致。
    /// </summary>
    [TestMethod]
    public async Task OpenOrBuild_WhenReachableCountExceedsBaseObjectCount_ArchivesAndRebuilds()
    {
        var root = CreateTestDirectory("DerivedReachableCount");
        try
        {
            var heapIndexDirectory = await CreatePublishedDerivedArtifactAsync(root);
            var derivedDirectory = Path.Combine(heapIndexDirectory, ".heapderived");
            var orderPath = Path.Combine(derivedDirectory, "dominator-order.bin");
            await using (var stream = new FileStream(orderPath, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(BitConverter.GetBytes(0));
            }

            var manifestPath = Path.Combine(derivedDirectory, "manifest.json");
            var manifest = await ReadJsonObjectAsync(manifestPath);
            manifest["ReachableObjectCount"] = 3;
            RefreshManifestFile(manifest, derivedDirectory, "dominator-order.bin");
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

            var recovered = HeapDerivedAnalysisArtifact.OpenOrBuild(heapIndexDirectory, CancellationToken.None);

            Assert.AreEqual(2, recovered.ObjectCount);
            Assert.HasCount(1, Directory.GetDirectories(heapIndexDirectory, ".heapderived.corrupt.*"));
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    /// <summary>
    /// 派生清单的摘要字段必须具有 SHA-256 结构，不能把非摘要文本交给延迟读取路径。
    /// </summary>
    [TestMethod]
    public async Task OpenOrBuild_WhenManifestHashIsMalformed_ArchivesAndRebuilds()
    {
        var root = CreateTestDirectory("DerivedMalformedHash");
        try
        {
            var heapIndexDirectory = await CreatePublishedDerivedArtifactAsync(root);
            var derivedDirectory = Path.Combine(heapIndexDirectory, ".heapderived");
            var manifestPath = Path.Combine(derivedDirectory, "manifest.json");
            var manifest = await ReadJsonObjectAsync(manifestPath);
            var files = manifest["Files"]?.AsArray()
                ?? throw new InvalidDataException("测试清单缺少 Files 数组。");
            files[0]!["Sha256"] = "not-a-sha256";
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

            var recovered = HeapDerivedAnalysisArtifact.OpenOrBuild(heapIndexDirectory, CancellationToken.None);

            Assert.AreEqual(2, recovered.ObjectCount);
            Assert.HasCount(1, Directory.GetDirectories(heapIndexDirectory, ".heapderived.corrupt.*"));
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    /// <summary>
    /// order 文件长度必须独立匹配 ReachableObjectCount，即使更新了文件摘要也不能复用多余记录。
    /// </summary>
    [TestMethod]
    public async Task OpenOrBuild_WhenOrderLengthDoesNotMatchReachableCount_ArchivesAndRebuilds()
    {
        var root = CreateTestDirectory("DerivedOrderLength");
        try
        {
            var heapIndexDirectory = await CreatePublishedDerivedArtifactAsync(root);
            var derivedDirectory = Path.Combine(heapIndexDirectory, ".heapderived");
            var orderPath = Path.Combine(derivedDirectory, "dominator-order.bin");
            await using (var stream = new FileStream(orderPath, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(BitConverter.GetBytes(0));
            }

            await RefreshManifestFileAsync(derivedDirectory, "dominator-order.bin");

            var recovered = HeapDerivedAnalysisArtifact.OpenOrBuild(heapIndexDirectory, CancellationToken.None);

            Assert.AreEqual(2, recovered.ObjectCount);
            Assert.AreEqual(2L * sizeof(int), new FileInfo(orderPath).Length);
            Assert.HasCount(1, Directory.GetDirectories(heapIndexDirectory, ".heapderived.corrupt.*"));
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    /// <summary>
    /// 自哈希且长度正确的 order 仍必须拒绝越界或重复 objectId，避免首次分页读取才抛异常或返回重复对象。
    /// </summary>
    /// <param name="malformation">要注入的 order 语义异常。</param>
    [TestMethod]
    [DataRow("out-of-range")]
    [DataRow("duplicate")]
    public async Task OpenOrBuild_WhenOrderEntriesAreInvalid_ArchivesAndRebuilds(string malformation)
    {
        var root = CreateTestDirectory($"DerivedOrderEntry.{malformation}");
        try
        {
            var heapIndexDirectory = await CreatePublishedDerivedArtifactAsync(root);
            var derivedDirectory = Path.Combine(heapIndexDirectory, ".heapderived");
            var orderPath = Path.Combine(derivedDirectory, "dominator-order.bin");
            WriteInt32Values(
                orderPath,
                malformation == "out-of-range" ? [2, 1] : [0, 0]);
            await RefreshManifestFileAsync(derivedDirectory, "dominator-order.bin");

            var recovered = HeapDerivedAnalysisArtifact.OpenOrBuild(heapIndexDirectory, CancellationToken.None);
            var page = recovered.ReadPage(0, 10, CancellationToken.None);

            Assert.AreEqual(2, recovered.ObjectCount);
            Assert.HasCount(2, page);
            Assert.AreNotEqual(page[0].ObjectId, page[1].ObjectId);
            Assert.HasCount(1, Directory.GetDirectories(heapIndexDirectory, ".heapderived.corrupt.*"));
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    /// <summary>
    /// 自哈希且长度正确的状态记录只允许 -2、-1 或基础对象范围内的直接支配者标识。
    /// </summary>
    /// <param name="invalidImmediateDominator">无效负哨兵或刚好越过对象上界的标识。</param>
    [TestMethod]
    [DataRow(-3)]
    [DataRow(2)]
    public async Task OpenOrBuild_WhenStateImmediateDominatorIsInvalid_ArchivesAndRebuilds(
        int invalidImmediateDominator)
    {
        var root = CreateTestDirectory($"DerivedStateEntry.{invalidImmediateDominator}");
        try
        {
            var heapIndexDirectory = await CreatePublishedDerivedArtifactAsync(root);
            var derivedDirectory = Path.Combine(heapIndexDirectory, ".heapderived");
            var statePath = Path.Combine(derivedDirectory, "dominator-state.bin");
            WriteInt32AtStart(statePath, invalidImmediateDominator);
            await RefreshManifestFileAsync(derivedDirectory, "dominator-state.bin");

            var recovered = HeapDerivedAnalysisArtifact.OpenOrBuild(heapIndexDirectory, CancellationToken.None);
            var page = recovered.ReadPage(0, 10, CancellationToken.None);

            Assert.HasCount(2, page);
            Assert.IsTrue(page.All(item => item.ImmediateDominatorObjectId is >= -2 and < 2));
            Assert.HasCount(1, Directory.GetDirectories(heapIndexDirectory, ".heapderived.corrupt.*"));
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    /// <summary>
    /// 数值仍在范围内但违反派生语义的状态不得复用：对象不能直接支配自身，
    /// order 不能引用不可达状态，且 reachable retained size 不能小于对象 shallow size。
    /// </summary>
    /// <param name="malformation">要注入的状态语义异常。</param>
    [TestMethod]
    [DataRow("self-dominator")]
    [DataRow("ordered-unreachable")]
    [DataRow("retained-below-shallow")]
    public async Task OpenOrBuild_WhenStateSemanticsAreInvalid_ArchivesAndRebuilds(string malformation)
    {
        var root = CreateTestDirectory($"DerivedStateSemantic.{malformation}");
        try
        {
            var heapIndexDirectory = await CreatePublishedDerivedArtifactAsync(root);
            var derivedDirectory = Path.Combine(heapIndexDirectory, ".heapderived");
            var statePath = Path.Combine(derivedDirectory, "dominator-state.bin");
            switch (malformation)
            {
                case "self-dominator":
                    WriteInt32AtOffset(statePath, 0, 0);
                    break;
                case "ordered-unreachable":
                    WriteInt32AtOffset(statePath, 0, -2);
                    break;
                case "retained-below-shallow":
                    WriteInt64AtOffset(statePath, sizeof(int) * 2, 0);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(malformation), malformation, "未知派生状态语义异常。");
            }

            await RefreshManifestFileAsync(derivedDirectory, "dominator-state.bin");

            var recovered = HeapDerivedAnalysisArtifact.OpenOrBuild(heapIndexDirectory, CancellationToken.None);

            Assert.AreEqual(2, recovered.ObjectCount);
            Assert.HasCount(2, recovered.ReadPage(0, 10, CancellationToken.None));
            Assert.HasCount(1, Directory.GetDirectories(heapIndexDirectory, ".heapderived.corrupt.*"));
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    /// <summary>
    /// 百万对象合法派生工件的热重开验证不得再按对象数分配托管位图；
    /// 临时验证位图必须位于 .heapderived 的精确同级目录，并在返回前完整清理。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void OpenOrBuild_WhenPublishedArtifactHasOneMillionObjects_KeepsHotReopenAllocationBounded()
    {
        var root = CreateTestDirectory("DerivedHotReopen");
        try
        {
            var heapIndexDirectory = WriteLargePublishedDerivedArtifact(root, HotReopenObjectCount);
            _ = HeapDerivedAnalysisArtifact.OpenOrBuild(heapIndexDirectory, CancellationToken.None);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

            var reopened = HeapDerivedAnalysisArtifact.OpenOrBuild(heapIndexDirectory, CancellationToken.None);

            var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Assert.AreEqual(HotReopenObjectCount, reopened.ObjectCount);
            Assert.IsLessThanOrEqualTo(
                MaximumHotReopenManagedAllocationBytes,
                allocatedBytes,
                $"Published derived artifact hot reopen allocated {allocatedBytes:N0} managed bytes.");
            Assert.IsTrue(Directory.Exists(Path.Combine(heapIndexDirectory, ".heapderived")));
            Assert.IsEmpty(Directory.GetDirectories(heapIndexDirectory, ".heapderived.validation.*.tmp"));
            Assert.IsEmpty(Directory.GetDirectories(heapIndexDirectory, ".heapderived.corrupt.*"));
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    /// <summary>
    /// 创建测试独占目录，避免工件恢复测试之间共享磁盘状态。
    /// </summary>
    /// <param name="scenario">用于诊断失败目录的场景名称。</param>
    /// <returns>已经创建的测试目录。</returns>
    private static string CreateTestDirectory(string scenario)
    {
        var path = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.{scenario}.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// 删除当前测试拥有的临时目录；生产快照和测试目录之外的证据不在清理范围内。
    /// </summary>
    /// <param name="path">测试独占目录。</param>
    private static void DeleteTestDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>
    /// 发布包含两个对象的真实基础索引并生成有效派生工件，供后续单点损坏测试使用。
    /// </summary>
    /// <param name="root">快照存储根目录。</param>
    /// <returns>基础堆索引目录。</returns>
    private static async Task<string> CreatePublishedDerivedArtifactAsync(string root)
    {
        var layout = new SnapshotStorageLayout(root);
        var snapshotId = MemorySnapshotId.New();
        var type = new TypeIdentity("Sample.Node", "Sample");
        var index = new SnapshotIndex(
        [
            new SnapshotIndex.ObjectRow(1, type, 16),
            new SnapshotIndex.ObjectRow(2, type, 24)
        ],
        new Dictionary<ulong, IReadOnlyList<ulong>> { [1] = [2] },
        roots: [1]);
        var router = new HeapIndexRouter(
            layout,
            maximumInMemoryObjectCount: 1,
            maximumInMemoryBytes: long.MaxValue);

        using var handle = await router.RouteAsync(snapshotId, index, CancellationToken.None);
        _ = await handle.GetDominatorPageAsync(0, 10, CancellationToken.None);
        return layout.GetHeapIndexDirectory(snapshotId);
    }

    /// <summary>
    /// 顺序写出全部由虚拟根直接支配的合法大工件，避免测试前置阶段运行支配树算法或保留 O(N) DTO。
    /// </summary>
    /// <param name="root">测试独占根目录。</param>
    /// <param name="objectCount">要写入的基础对象和可达对象数量。</param>
    /// <returns>包含 objects.bin 与已发布 .heapderived 的基础索引目录。</returns>
    private static string WriteLargePublishedDerivedArtifact(string root, int objectCount)
    {
        var heapIndexDirectory = Path.Combine(root, "snapshot", ".heapidx");
        var derivedDirectory = Path.Combine(heapIndexDirectory, ".heapderived");
        Directory.CreateDirectory(derivedDirectory);
        var objectPath = Path.Combine(heapIndexDirectory, "objects.bin");
        var statePath = Path.Combine(derivedDirectory, "dominator-state.bin");
        var orderPath = Path.Combine(derivedDirectory, "dominator-order.bin");
        using (var objects = new BinaryWriter(new FileStream(objectPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan)))
        using (var state = new BinaryWriter(new FileStream(statePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan)))
        using (var order = new BinaryWriter(new FileStream(orderPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan)))
        {
            for (var objectId = 0; objectId < objectCount; objectId++)
            {
                objects.Write((ulong)objectId + 1);
                objects.Write(0);
                objects.Write(16L);
                state.Write(-1);
                state.Write(0);
                state.Write(16L);
                order.Write(objectId);
            }
        }

        var manifest = new JsonObject
        {
            ["Version"] = 1,
            ["Completed"] = true,
            ["ReachableObjectCount"] = objectCount,
            ["Files"] = new JsonArray(
                CreateDerivedManifestFile(statePath),
                CreateDerivedManifestFile(orderPath))
        };
        File.WriteAllText(Path.Combine(derivedDirectory, "manifest.json"), manifest.ToJsonString());
        return heapIndexDirectory;
    }

    /// <summary>
    /// 为测试生成的派生数据文件创建与生产清单兼容的名称、长度和摘要条目。
    /// </summary>
    /// <param name="path">已关闭写入句柄的派生数据文件。</param>
    /// <returns>可直接放入清单 Files 数组的 JSON 条目。</returns>
    private static JsonObject CreateDerivedManifestFile(string path) => new()
    {
        ["Name"] = Path.GetFileName(path),
        ["Length"] = new FileInfo(path).Length,
        ["Sha256"] = ComputeHash(path)
    };

    /// <summary>
    /// 读取清单对象，并在测试夹具本身损坏时立即报告明确失败。
    /// </summary>
    /// <param name="path">清单路径。</param>
    /// <returns>可修改的 JSON 对象。</returns>
    private static async Task<JsonObject> ReadJsonObjectAsync(string path) =>
        JsonNode.Parse(await File.ReadAllTextAsync(path))?.AsObject()
        ?? throw new InvalidDataException("测试清单不是 JSON 对象。");

    /// <summary>
    /// 注入一种清单文件集合畸形；期望值由固定文件名和独立 SHA-256 计算产生。
    /// </summary>
    /// <param name="manifest">有效清单。</param>
    /// <param name="malformation">畸形类型。</param>
    /// <param name="heapIndexDirectory">基础索引目录。</param>
    /// <param name="derivedDirectory">派生索引目录。</param>
    private static void CorruptManifestEntries(
        JsonObject manifest,
        string malformation,
        string heapIndexDirectory,
        string derivedDirectory)
    {
        var files = manifest["Files"]?.AsArray()
            ?? throw new InvalidDataException("测试清单缺少 Files 数组。");
        switch (malformation)
        {
            case "null-list":
                manifest["Files"] = null;
                break;
            case "null-entry":
                files[0] = null;
                break;
            case "duplicate":
                files[1] = files[0]?.DeepClone();
                break;
            case "unexpected":
                File.Copy(
                    Path.Combine(derivedDirectory, "dominator-state.bin"),
                    Path.Combine(derivedDirectory, "unexpected.bin"));
                files[0]!["Name"] = "unexpected.bin";
                break;
            case "missing":
                files.RemoveAt(1);
                break;
            case "traversal":
                var objectsPath = Path.Combine(heapIndexDirectory, "objects.bin");
                files[0]!["Name"] = "../objects.bin";
                files[0]!["Length"] = new FileInfo(objectsPath).Length;
                files[0]!["Sha256"] = ComputeHash(objectsPath);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(malformation), malformation, "未知清单畸形类型。");
        }
    }

    /// <summary>
    /// 重新计算单个派生文件的长度和摘要并持久化清单，使测试只暴露结构不一致而非摘要不一致。
    /// </summary>
    /// <param name="derivedDirectory">派生索引目录。</param>
    /// <param name="name">固定派生文件名。</param>
    private static async Task RefreshManifestFileAsync(string derivedDirectory, string name)
    {
        var manifestPath = Path.Combine(derivedDirectory, "manifest.json");
        var manifest = await ReadJsonObjectAsync(manifestPath);
        RefreshManifestFile(manifest, derivedDirectory, name);
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
    }

    /// <summary>
    /// 在固定偏移覆盖一个 32 位状态字段，不改变文件长度。
    /// </summary>
    private static void WriteInt32AtOffset(string path, long offset, int value)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Position = offset;
        using var writer = new BinaryWriter(stream);
        writer.Write(value);
    }

    /// <summary>
    /// 在固定偏移覆盖一个 64 位状态字段，不改变文件长度。
    /// </summary>
    private static void WriteInt64AtOffset(string path, long offset, long value)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Position = offset;
        using var writer = new BinaryWriter(stream);
        writer.Write(value);
    }

    /// <summary>
    /// 在内存清单中更新指定文件的长度和摘要，找不到固定条目时视为测试夹具错误。
    /// </summary>
    /// <param name="manifest">有效清单。</param>
    /// <param name="directory">派生索引目录。</param>
    /// <param name="name">固定派生文件名。</param>
    private static void RefreshManifestFile(JsonObject manifest, string directory, string name)
    {
        var files = manifest["Files"]?.AsArray()
            ?? throw new InvalidDataException("测试清单缺少 Files 数组。");
        var entry = files
            .Select(static node => node?.AsObject())
            .SingleOrDefault(item => string.Equals(item?["Name"]?.GetValue<string>(), name, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"测试清单缺少 {name} 条目。");
        var path = Path.Combine(directory, name);
        entry["Length"] = new FileInfo(path).Length;
        entry["Sha256"] = ComputeHash(path);
    }

    /// <summary>
    /// 以 BinaryWriter 使用的固定小端格式完整替换 order 记录。
    /// </summary>
    /// <param name="path">order 文件路径。</param>
    /// <param name="values">按分页顺序写入的对象标识。</param>
    private static void WriteInt32Values(string path, IReadOnlyList<int> values)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        foreach (var value in values)
        {
            writer.Write(value);
        }
    }

    /// <summary>
    /// 只覆写状态文件第一条记录的直接支配者标识，保留其余结构和长度不变。
    /// </summary>
    /// <param name="path">状态文件路径。</param>
    /// <param name="value">要写入的直接支配者标识。</param>
    private static void WriteInt32AtStart(string path, int value)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        writer.Write(value);
    }

    /// <summary>
    /// 独立计算测试文件摘要，避免用生产清单实现生成期望结果。
    /// </summary>
    /// <param name="path">待计算文件路径。</param>
    /// <returns>大写十六进制 SHA-256。</returns>
    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
