using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Tests.Diagnostics;

/// <summary>
/// 验证支配树构建状态落在受控文件映射工作区中，而不是按对象数扩张多个托管数组。
/// </summary>
[TestClass]
[DoNotParallelize]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "测试名称描述场景。")]
public sealed class HeapDominatorWorkspaceTests
{
    private const int AllocationProbeObjectCount = 250_000;
    private const long MaximumManagedAllocationBytes = 12L * 1024 * 1024;

    /// <summary>
    /// objectId 到 DFS 编号以及 LT 节点状态必须能在同一个固定长度工作区中随机读写，
    /// 释放工作区后文件句柄应立即归还给拥有者清理。
    /// </summary>
    [TestMethod]
    public void Create_WhenStateIsWritten_RoundTripsThroughFixedFileBackedLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.DominatorWorkspace.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "workspace.bin");
        try
        {
            using (var workspace = HeapDominatorWorkspace.Create(
                path,
                objectCount: 4,
                budgetBytes: HeapIndexResourcePolicy.MaximumBudgetBytes))
            {
                workspace.SetDfsNumber(2, 3);
                workspace.WriteNode(
                    3,
                    new HeapDominatorNodeState(
                        ObjectId: 2,
                        Parent: 1,
                        Semi: 2,
                        ImmediateDominator: 1,
                        Ancestor: 1,
                        Label: 3,
                        BucketHead: 4,
                        BucketNext: 5,
                        Scratch: 6,
                        IsRoot: true,
                        RetainedSizeBytes: 128));

                Assert.AreEqual(3, workspace.GetDfsNumber(2));
                Assert.AreEqual(
                    new HeapDominatorNodeState(2, 1, 2, 1, 1, 3, 4, 5, 6, true, 128),
                    workspace.ReadNode(3));
                Assert.AreEqual(HeapDominatorWorkspace.CalculateLengthBytes(4), new FileInfo(path).Length);
            }

            File.Delete(path);
            Assert.IsFalse(File.Exists(path));
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
    /// LT 记录跨越窗口边界时，文件工作区必须完整往返所有字段，且任一时刻只保留调用方指定的有界视图。
    /// </summary>
    [TestMethod]
    public void Create_WhenNodeRecordCrossesWindowBoundary_RoundTripsWithinBoundedView()
    {
        const int windowBytes = 64 * 1024;
        const int objectCount = 16_361;
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.DominatorWorkspaceBoundary.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "workspace.bin");
        try
        {
            using var workspace = HeapDominatorWorkspace.Create(
                path,
                objectCount,
                HeapIndexResourcePolicy.MaximumBudgetBytes,
                windowBytes);
            var expected = new HeapDominatorNodeState(0, 0, 1, 0, 0, 1, 0, 0, 0, false, 0x0102030405060708);

            workspace.WriteNode(1, expected);

            Assert.AreEqual(expected, workspace.ReadNode(1));
            Assert.AreEqual(windowBytes, workspace.MaximumMappedWindowBytes);
            Assert.IsLessThanOrEqualTo(windowBytes, workspace.CurrentMappedWindowBytes);
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
    /// 整条 LT 节点状态的读写必须各通过一次固定记录访问完成，不能退化为按字段重复调用映射访问器；
    /// 该结构性约束不依赖执行机器速度，并直接保护千万对象 Dominator 的热循环成本。
    /// </summary>
    [TestMethod]
    public void ReadAndWriteNode_WhenWholeStateIsAccessed_UseOneRecordOperationInsteadOfScalarFieldOperations()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.DominatorWorkspaceBatch.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "workspace.bin");
        try
        {
            using var workspace = HeapDominatorWorkspace.Create(
                path,
                objectCount: 4,
                budgetBytes: HeapIndexResourcePolicy.MaximumBudgetBytes);
            var expected = new HeapDominatorNodeState(2, 1, 2, 1, 1, 3, 4, 5, 6, true, 128);

            workspace.WriteNode(3, expected);
            var actual = workspace.ReadNode(3);

            Assert.AreEqual(expected, actual);
            Assert.AreEqual(1L, workspace.NodeRecordWriteOperations);
            Assert.AreEqual(1L, workspace.NodeRecordReadOperations);
            Assert.AreEqual(0L, workspace.NodeScalarWriteOperations);
            Assert.AreEqual(0L, workspace.NodeScalarReadOperations);
            Assert.AreEqual(1L, workspace.NodeViewOpenOperations);
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
    /// 可写 LT 工作区和只读图窗口必须共享同一个 64 MiB 进程配额，
    /// 避免两类映射各自满足局部限制却共同突破全局上限。
    /// </summary>
    [TestMethod]
    public void Create_WhenReadWindowsConsumeRemainingBudget_SharesGlobalMappedWindowLimit()
    {
        const int readerCount = 7;
        const int workspaceObjectCount = 200_000;
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.DominatorSharedWindowBudget.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var readers = new List<HeapMappedReadWindow>();
        try
        {
            for (var index = 0; index < readerCount; index++)
            {
                var readerPath = Path.Combine(root, $"reader-{index}.bin");
                using (var stream = File.Create(readerPath))
                {
                    stream.SetLength(HeapIndexResourcePolicy.DefaultMappedReadWindowBytes);
                }

                var reader = new HeapMappedReadWindow(readerPath);
                _ = reader.ReadInt32(0);
                readers.Add(reader);
            }

            var workspacePath = Path.Combine(root, "workspace.bin");
            using var workspace = HeapDominatorWorkspace.Create(
                workspacePath,
                workspaceObjectCount,
                HeapIndexResourcePolicy.MaximumBudgetBytes);
            workspace.SetDfsNumber(0, 1);
            var rejectedPath = Path.Combine(root, "rejected.bin");
            using (var stream = File.Create(rejectedPath))
            {
                stream.SetLength(HeapIndexResourcePolicy.DefaultMappedReadWindowBytes);
            }

            using var rejected = new HeapMappedReadWindow(rejectedPath);
            var exception = Assert.ThrowsExactly<DiagnosticsException>(() => rejected.ReadInt32(0));

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotQueryLimitReached, exception.ErrorCode);
            Assert.IsLessThanOrEqualTo(
                HeapIndexResourcePolicy.DefaultMappedReadWindowBytes,
                workspace.CurrentMappedWindowBytes);
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 工作区本身超过调用方预算时必须在创建映射和写入大文件前稳定返回派生分析不可用，
    /// 不能依赖后续 OOM 或磁盘写入失败来中止。
    /// </summary>
    [TestMethod]
    public void Create_WhenWorkspaceExceedsBudget_ReturnsDerivedAnalysisUnavailableBeforeCreatingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.DominatorWorkspace.Refused.{Guid.NewGuid():N}.bin");
        try
        {
            var exception = Assert.ThrowsExactly<DiagnosticsException>(
                () => HeapDominatorWorkspace.Create(path, objectCount: 10_000_000, budgetBytes: 1));

            Assert.AreEqual(DiagnosticsErrorCode.DerivedAnalysisUnavailable, exception.ErrorCode);
            Assert.IsFalse(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// 映射大图的派生阶段不得再为 DFS、LT 状态、根和排序结果同时分配多组 O(N) 托管数组；
    /// 250k 链图的全进程托管分配应保持在固定排序块和小型元数据范围内。
    /// </summary>
    [TestMethod]
    public void OpenOrBuild_WhenMappedGraphIsLarge_KeepsManagedAllocationBelowObjectScaledArrayFootprint()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.DominatorAllocation.{Guid.NewGuid():N}");
        var heapIndexDirectory = Path.Combine(root, "snapshot", ".heapidx");
        Directory.CreateDirectory(heapIndexDirectory);
        try
        {
            WriteChainGraphArtifacts(heapIndexDirectory, AllocationProbeObjectCount);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

            var artifact = HeapDerivedAnalysisArtifact.OpenOrBuild(heapIndexDirectory, CancellationToken.None);

            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            Assert.AreEqual(AllocationProbeObjectCount, artifact.ObjectCount);
            Assert.IsLessThanOrEqualTo(
                MaximumManagedAllocationBytes,
                allocatedBytes,
                $"Dominator derivation allocated {allocatedBytes:N0} managed bytes for {AllocationProbeObjectCount:N0} mapped objects.");
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
    /// 并发读取占满全局映射窗口时，派生查询边界必须返回稳定的 DerivedAnalysisUnavailable，
    /// 同时保留底层资源错误且清理本次工作区，不能把基础查询限流码泄漏给 Dominator 调用方。
    /// </summary>
    [TestMethod]
    public void OpenOrBuild_WhenMappedWindowBudgetIsOccupied_ReturnsDerivedAnalysisUnavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.DominatorMappedBudget.{Guid.NewGuid():N}");
        var heapIndexDirectory = Path.Combine(root, "snapshot", ".heapidx");
        Directory.CreateDirectory(heapIndexDirectory);
        var leases = new List<HeapMappedReadWindow>();
        try
        {
            WriteChainGraphArtifacts(heapIndexDirectory, 2);
            var leaseCount = checked((int)(
                HeapIndexResourcePolicy.MaximumMappedWindowBytes
                / HeapIndexResourcePolicy.DefaultMappedReadWindowBytes));
            for (var index = 0; index < leaseCount; index++)
            {
                var path = Path.Combine(root, $"lease-{index}.bin");
                using (var stream = File.Create(path))
                {
                    stream.SetLength(HeapIndexResourcePolicy.DefaultMappedReadWindowBytes);
                }

                var lease = new HeapMappedReadWindow(path);
                _ = lease.ReadInt32(0);
                leases.Add(lease);
            }

            var exception = Assert.ThrowsExactly<DiagnosticsException>(
                () => HeapDerivedAnalysisArtifact.OpenOrBuild(heapIndexDirectory, CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.DerivedAnalysisUnavailable, exception.ErrorCode);
            Assert.IsInstanceOfType<DiagnosticsException>(exception.InnerException);
            Assert.AreEqual(
                DiagnosticsErrorCode.SnapshotQueryLimitReached,
                ((DiagnosticsException)exception.InnerException).ErrorCode);
            Assert.IsFalse(Directory.Exists(Path.Combine(heapIndexDirectory, ".heapderived")));
            Assert.IsEmpty(Directory.GetDirectories(Path.GetDirectoryName(heapIndexDirectory)!, ".heapderived.*.tmp"));
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 已发布工件热重开的验证位图无法取得共享映射配额时，也必须由 Dominator 边界统一翻译资源错误，
    /// 不能因为失败发生在复用路径就向上层泄漏 SnapshotQueryLimitReached。
    /// </summary>
    [TestMethod]
    public void OpenOrBuild_WhenPublishedArtifactValidationCannotLeaseWindow_ReturnsDerivedAnalysisUnavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.DominatorValidationBudget.{Guid.NewGuid():N}");
        var heapIndexDirectory = Path.Combine(root, "snapshot", ".heapidx");
        Directory.CreateDirectory(heapIndexDirectory);
        var leases = new List<HeapMappedReadWindow>();
        try
        {
            WriteChainGraphArtifacts(heapIndexDirectory, 2);
            _ = HeapDerivedAnalysisArtifact.OpenOrBuild(heapIndexDirectory, CancellationToken.None);
            var leaseCount = checked((int)(
                HeapIndexResourcePolicy.MaximumMappedWindowBytes
                / HeapIndexResourcePolicy.DefaultMappedReadWindowBytes));
            for (var index = 0; index < leaseCount; index++)
            {
                var path = Path.Combine(root, $"validation-lease-{index}.bin");
                using (var stream = File.Create(path))
                {
                    stream.SetLength(HeapIndexResourcePolicy.DefaultMappedReadWindowBytes);
                }

                var lease = new HeapMappedReadWindow(path);
                _ = lease.ReadInt32(0);
                leases.Add(lease);
            }

            var exception = Assert.ThrowsExactly<DiagnosticsException>(
                () => HeapDerivedAnalysisArtifact.OpenOrBuild(heapIndexDirectory, CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.DerivedAnalysisUnavailable, exception.ErrorCode);
            Assert.IsInstanceOfType<DiagnosticsException>(exception.InnerException);
            Assert.AreEqual(
                DiagnosticsErrorCode.SnapshotQueryLimitReached,
                ((DiagnosticsException)exception.InnerException).ErrorCode);
            Assert.IsTrue(Directory.Exists(Path.Combine(heapIndexDirectory, ".heapderived")));
            Assert.IsEmpty(Directory.GetDirectories(heapIndexDirectory, ".heapderived.validation.*.tmp"));
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 顺序写出一条单根链图所需的固定宽度基础工件，避免测试夹具自身构造对象或边集合。
    /// </summary>
    private static void WriteChainGraphArtifacts(string directory, int objectCount)
    {
        const int unknownRootEvidenceBytes = sizeof(byte) + sizeof(int) * 3;
        using (var writer = new BinaryWriter(File.Create(Path.Combine(directory, "objects.bin"))))
        {
            for (var objectId = 0; objectId < objectCount; objectId++)
            {
                writer.Write((ulong)objectId + 1);
                writer.Write(0);
                writer.Write(16L);
            }
        }

        using (var forwardOffsets = new BinaryWriter(File.Create(Path.Combine(directory, "forward-offsets.bin"))))
        using (var reverseOffsets = new BinaryWriter(File.Create(Path.Combine(directory, "reverse-offsets.bin"))))
        using (var roots = new BinaryWriter(File.Create(Path.Combine(directory, "roots-by-object.bin"))))
        {
            for (var offsetIndex = 0; offsetIndex <= objectCount; offsetIndex++)
            {
                forwardOffsets.Write((long)Math.Min(offsetIndex, objectCount - 1));
                reverseOffsets.Write((long)Math.Max(0, offsetIndex - 1));
                roots.Write(offsetIndex == 0 ? 0L : unknownRootEvidenceBytes);
            }
        }

        using (var evidence = new BinaryWriter(File.Create(Path.Combine(directory, "root-evidence.bin"))))
        {
            evidence.Write((byte)MemoryRootKind.Unknown);
            evidence.Write((int)MemoryRootFlags.None);
            evidence.Write(-1);
            evidence.Write(-1);
        }

        using (var forwardTargets = new BinaryWriter(File.Create(Path.Combine(directory, "forward-targets.bin"))))
        using (var reverseTargets = new BinaryWriter(File.Create(Path.Combine(directory, "reverse-targets.bin"))))
        {
            for (var objectId = 0; objectId < objectCount - 1; objectId++)
            {
                forwardTargets.Write(objectId + 1);
                reverseTargets.Write(objectId);
            }
        }
    }
}
