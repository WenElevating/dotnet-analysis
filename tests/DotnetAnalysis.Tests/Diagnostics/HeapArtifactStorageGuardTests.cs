using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;
using System.Diagnostics.CodeAnalysis;

namespace DotnetAnalysis.Tests.Diagnostics;

/// <summary>
/// 验证堆索引工件族的容量保护不会以删除历史快照作为腾挪空间的手段。
/// </summary>
[TestClass]
[DoNotParallelize]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "测试名表达被验证的行为。")]
public sealed class HeapArtifactStorageGuardTests
{
    /// <summary>
    /// 派生工件已完成写入但实际容量复核失败时，支配树边界必须保留 SnapshotStorageLimitReached，
    /// 不能将明确的磁盘治理结果改写为笼统的 DerivedAnalysisUnavailable。
    /// </summary>
    [TestMethod]
    public void OpenOrBuild_WhenDerivedActualUsageExceedsAvailableSpace_PreservesStorageLimitError()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        var layout = new SnapshotStorageLayout(root);
        var heapIndexDirectory = Path.Combine(root, MemorySnapshotId.New().ToString(), ".heapidx");
        Directory.CreateDirectory(heapIndexDirectory);
        var availableSpaceReadCount = 0;
        try
        {
            WriteTwoObjectChainArtifacts(heapIndexDirectory);
            var estimatedBytes = HeapArtifactStorageGuard.EstimateDerivedBuildPeakBytes(2);
            var guard = new HeapArtifactStorageGuard(
                layout,
                _ => Interlocked.Increment(ref availableSpaceReadCount) == 1
                    ? HeapArtifactStorageGuard.MinimumFreeVolumeBytes + estimatedBytes
                    : HeapArtifactStorageGuard.MinimumFreeVolumeBytes - 1);

            var exception = Assert.ThrowsExactly<DiagnosticsException>(() =>
                HeapDerivedAnalysisArtifact.OpenOrBuild(
                    heapIndexDirectory,
                    CancellationToken.None,
                    guard));

            Assert.AreEqual(
                DiagnosticsErrorCode.SnapshotStorageLimitReached,
                exception.ErrorCode,
                exception.ToString());
            Assert.IsFalse(Directory.Exists(Path.Combine(heapIndexDirectory, ".heapderived")));
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
    /// 首个发布实际增长超过估算时，实际复核仍必须保留另一活动租约，不能共同侵占卷余量。
    /// </summary>
    [TestMethod]
    public async Task ReconcileActualUsageAsync_WhenActualGrowthExceedsEstimate_PreservesOtherReservationHeadroom()
    {
        const long estimatedGrowthBytes = 4096;
        const long actualGrowthBytes = estimatedGrowthBytes * 2;
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        var firstIndexDirectory = Path.Combine(root, MemorySnapshotId.New().ToString(), ".heapidx");
        var secondIndexDirectory = Path.Combine(root, MemorySnapshotId.New().ToString(), ".heapidx");
        var firstTemporaryDirectory = Path.Combine(Path.GetDirectoryName(firstIndexDirectory)!, ".heapidx.first.tmp");
        Directory.CreateDirectory(firstTemporaryDirectory);
        var availableBytes = HeapArtifactStorageGuard.MinimumFreeVolumeBytes + estimatedGrowthBytes * 2;
        try
        {
            var firstGuard = new HeapArtifactStorageGuard(new SnapshotStorageLayout(root), _ => availableBytes);
            var secondGuard = new HeapArtifactStorageGuard(new SnapshotStorageLayout(root), _ => availableBytes);
            using var firstReservation = await firstGuard.ReservePublicationAsync(
                firstIndexDirectory,
                estimatedGrowthBytes,
                CancellationToken.None);
            using var secondReservation = await secondGuard.ReservePublicationAsync(
                secondIndexDirectory,
                estimatedGrowthBytes,
                CancellationToken.None);
            await File.WriteAllBytesAsync(
                Path.Combine(firstTemporaryDirectory, "oversized.bin"),
                new byte[actualGrowthBytes]);
            availableBytes = HeapArtifactStorageGuard.MinimumFreeVolumeBytes;

            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                async () => await firstReservation.ReconcileActualUsageAsync(
                    firstTemporaryDirectory,
                    CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotStorageLimitReached, exception.ErrorCode);
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
    /// 同一卷上的两个已知峰值不能同时占用同一份余量；首个租约释放后，后续发布可重新预留。
    /// </summary>
    [TestMethod]
    public async Task ReservePublicationAsync_WhenVolumeCapacityIsReserved_RejectsContentionUntilReleased()
    {
        const long estimatedGrowthBytes = 4096;
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        var firstIndexDirectory = Path.Combine(root, MemorySnapshotId.New().ToString(), ".heapidx");
        var secondIndexDirectory = Path.Combine(firstIndexDirectory, ".heapderived");
        Directory.CreateDirectory(root);
        try
        {
            var availableBytes = HeapArtifactStorageGuard.MinimumFreeVolumeBytes + estimatedGrowthBytes;
            var firstGuard = new HeapArtifactStorageGuard(new SnapshotStorageLayout(root), _ => availableBytes);
            var secondGuard = new HeapArtifactStorageGuard(new SnapshotStorageLayout(root), _ => availableBytes);

            using var firstReservation = await firstGuard.ReservePublicationAsync(
                firstIndexDirectory,
                estimatedGrowthBytes,
                CancellationToken.None);
            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                async () => await secondGuard.ReservePublicationAsync(
                    secondIndexDirectory,
                    estimatedGrowthBytes,
                    CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotStorageLimitReached, exception.ErrorCode);
            firstReservation.Dispose();
            using var retryReservation = await secondGuard.ReservePublicationAsync(
                secondIndexDirectory,
                estimatedGrowthBytes,
                CancellationToken.None);
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
    /// 工件开始增长前必须把已知峰值纳入卷余量和工件族配额；临界值允许，少一字节余量或多一字节增长时拒绝。
    /// </summary>
    [TestMethod]
    public async Task EnsureCanStartPublicationAsync_EnforcesEstimatedGrowthAtCriticalFreeSpaceBoundary()
    {
        const long estimatedGrowthBytes = 4096;
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        var snapshotDirectory = Path.Combine(root, MemorySnapshotId.New().ToString());
        var indexDirectory = Path.Combine(snapshotDirectory, ".heapidx");
        Directory.CreateDirectory(snapshotDirectory);
        try
        {
            var allowed = new HeapArtifactStorageGuard(
                new SnapshotStorageLayout(root),
                static _ => HeapArtifactStorageGuard.MinimumFreeVolumeBytes + estimatedGrowthBytes);
            await allowed.EnsureCanStartPublicationAsync(indexDirectory, estimatedGrowthBytes, CancellationToken.None);
            var familyBoundary = new HeapArtifactStorageGuard(
                new SnapshotStorageLayout(root),
                static _ => long.MaxValue);
            await familyBoundary.EnsureCanStartPublicationAsync(
                indexDirectory,
                HeapArtifactStorageGuard.MaximumArtifactFamilyBytes,
                CancellationToken.None);
            var rejected = new HeapArtifactStorageGuard(
                new SnapshotStorageLayout(root),
                static _ => HeapArtifactStorageGuard.MinimumFreeVolumeBytes + estimatedGrowthBytes - 1);

            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                async () => await rejected.EnsureCanStartPublicationAsync(
                    indexDirectory,
                    estimatedGrowthBytes,
                    CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotStorageLimitReached, exception.ErrorCode);
            var familyException = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                async () => await familyBoundary.EnsureCanStartPublicationAsync(
                    indexDirectory,
                    HeapArtifactStorageGuard.MaximumArtifactFamilyBytes + 1,
                    CancellationToken.None));
            Assert.AreEqual(DiagnosticsErrorCode.SnapshotStorageLimitReached, familyException.ErrorCode);
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
    /// 当索引所在卷不能保留系统所需的最小空闲空间时，新的工件发布必须稳定失败，且已有证据保持原样。
    /// </summary>
    [TestMethod]
    public async Task EnsureCanPublishAsync_WhenVolumeFreeSpaceIsBelowMinimum_PreservesExistingEvidenceAndFails()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        var snapshotDirectory = Path.Combine(root, MemorySnapshotId.New().ToString());
        var indexDirectory = Path.Combine(snapshotDirectory, ".heapidx");
        Directory.CreateDirectory(indexDirectory);
        var evidence = Path.Combine(snapshotDirectory, "snapshot.gcdump");
        await File.WriteAllBytesAsync(evidence, [1, 2, 3]);
        try
        {
            var guard = new HeapArtifactStorageGuard(new SnapshotStorageLayout(root), static _ => 0);

            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                async () => await guard.EnsureCanPublishAsync(indexDirectory, null, CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotStorageLimitReached, exception.ErrorCode);
            Assert.IsTrue(File.Exists(evidence));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(evidence));
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
    /// 顺序写出两个对象的一条单根链基础工件，供派生发布容量边界运行真实构建与实际复核。
    /// </summary>
    /// <param name="directory">已创建的 .heapidx 测试目录。</param>
    private static void WriteTwoObjectChainArtifacts(string directory)
    {
        const int unknownRootEvidenceBytes = sizeof(byte) + sizeof(int) * 3;
        using (var objects = new BinaryWriter(File.Create(Path.Combine(directory, "objects.bin"))))
        {
            for (var objectId = 0; objectId < 2; objectId++)
            {
                objects.Write((ulong)objectId + 1);
                objects.Write(0);
                objects.Write(16L);
            }
        }

        using (var forwardOffsets = new BinaryWriter(File.Create(Path.Combine(directory, "forward-offsets.bin"))))
        using (var reverseOffsets = new BinaryWriter(File.Create(Path.Combine(directory, "reverse-offsets.bin"))))
        using (var roots = new BinaryWriter(File.Create(Path.Combine(directory, "roots-by-object.bin"))))
        {
            forwardOffsets.Write(0L);
            forwardOffsets.Write(1L);
            forwardOffsets.Write(1L);
            reverseOffsets.Write(0L);
            reverseOffsets.Write(0L);
            reverseOffsets.Write(1L);
            roots.Write(0L);
            roots.Write((long)unknownRootEvidenceBytes);
            roots.Write((long)unknownRootEvidenceBytes);
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
            forwardTargets.Write(1);
            reverseTargets.Write(0);
        }
    }
}
