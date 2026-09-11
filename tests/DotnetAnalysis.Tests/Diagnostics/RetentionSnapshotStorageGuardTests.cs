using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Tests.Diagnostics;

/// <summary>
/// 验证保留分析快照在写入前执行独立的容量保护，而不删除已有函数证据。
/// </summary>
[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain incorrect suffix", Justification = "测试名表达被验证的行为。")]
public sealed class RetentionSnapshotStorageGuardTests
{
    /// <summary>
    /// 容量保护必须是可替换的诊断层组件，便于在实际卷状态之外验证边界拒绝。
    /// </summary>
    [TestMethod]
    public void RetentionSnapshotStorageGuard_ExistsAsAnIndependentStorageProtectionComponent()
    {
        var guardType = typeof(MemorySnapshotStore).Assembly.GetType(
            "DotnetAnalysis.Diagnostics.Windows.RetentionSnapshotStorageGuard");

        Assert.IsNotNull(guardType);
    }

    /// <summary>
    /// 同一受管目录的两次提升保留必须串行化，以便第二次容量检查看到第一份已提升证据。
    /// </summary>
    [TestMethod]
    public async Task ReservePromotionAsync_WhenAnotherReservationIsHeld_WaitsUntilItIsReleased()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var firstPath = Path.Combine(root, "first.retentionheap");
            var secondPath = Path.Combine(root, "second.retentionheap");
            await File.WriteAllBytesAsync(firstPath, [1]);
            await File.WriteAllBytesAsync(secondPath, [2]);
            var firstGuard = new RetentionSnapshotStorageGuard(layout, static _ => long.MaxValue);
            var secondGuard = new RetentionSnapshotStorageGuard(layout, static _ => long.MaxValue);

            using var first = await firstGuard.ReservePromotionAsync(firstPath, CancellationToken.None);
            var second = secondGuard.ReservePromotionAsync(secondPath, CancellationToken.None);
            await Task.Delay(50);

            Assert.IsFalse(second.IsCompleted);
            first.Dispose();
            using var secondReservation = await second;
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
    /// 捕获级租约必须在原始 spool 创建前串行化同一受管目录，并一直持有到调用方显式释放。
    /// </summary>
    [TestMethod]
    public async Task ReserveCaptureAsync_WhenAnotherCaptureReservationIsHeld_WaitsUntilItIsReleased()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var firstGuard = new RetentionSnapshotStorageGuard(layout, static _ => long.MaxValue);
            var secondGuard = new RetentionSnapshotStorageGuard(layout, static _ => long.MaxValue);

            using var first = await firstGuard.ReserveCaptureAsync(CancellationToken.None);
            var second = secondGuard.ReserveCaptureAsync(CancellationToken.None);
            await Task.Delay(50);

            Assert.IsFalse(second.IsCompleted);
            first.Dispose();
            using var secondReservation = await second;
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
    /// 调用方未取消时，跨进程存储锁等待必须在有限窗口内以稳定诊断错误结束，不能无限挂起捕获。
    /// </summary>
    [TestMethod]
    public async Task ReserveCaptureAsync_WhenStorageLockRemainsHeld_TimesOutWithStableError()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var cleanupCancellation = new CancellationTokenSource();
        IRetentionSnapshotCaptureReservation? first = null;
        Task<IRetentionSnapshotCaptureReservation>? second = null;
        try
        {
            var layout = new SnapshotStorageLayout(root);
            first = await new RetentionSnapshotStorageGuard(layout, static _ => long.MaxValue)
                .ReserveCaptureAsync(CancellationToken.None);
            second = new RetentionSnapshotStorageGuard(layout, static _ => long.MaxValue)
                .ReserveCaptureAsync(cleanupCancellation.Token);
            var stopwatch = Stopwatch.StartNew();

            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(async () =>
                await second.WaitAsync(TimeSpan.FromSeconds(3)));

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotStorageLimitReached, exception.ErrorCode);
            Assert.IsLessThan(TimeSpan.FromSeconds(3), stopwatch.Elapsed);
        }
        finally
        {
            cleanupCancellation.Cancel();
            first?.Dispose();
            if (second is not null)
            {
                try
                {
                    using var unexpectedReservation = await second;
                }
                catch (OperationCanceledException)
                {
                }
                catch (DiagnosticsException)
                {
                }
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 调用方取消必须中止锁等待并保留取消语义，不能被内部有限超时改写为存储错误。
    /// </summary>
    [TestMethod]
    public async Task ReserveCaptureAsync_WhenCallerCancelsLockWait_PreservesCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var layout = new SnapshotStorageLayout(root);
            using var first = await new RetentionSnapshotStorageGuard(layout, static _ => long.MaxValue)
                .ReserveCaptureAsync(CancellationToken.None);
            using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await new RetentionSnapshotStorageGuard(layout, static _ => long.MaxValue)
                    .ReserveCaptureAsync(cancellationSource.Token));
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
    /// 失败捕获归档的原始证据必须计入总配额；拒绝新捕获时不得删除或截断已有证据。
    /// </summary>
    [TestMethod]
    public async Task EnsureCanStartAsync_WhenRawEvidenceReachesQuota_RejectsWithoutDeletingEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        var evidenceDirectory = Path.Combine(root, $".retention-raw-evidence.{Guid.NewGuid():N}");
        Directory.CreateDirectory(evidenceDirectory);
        var evidencePath = Path.Combine(evidenceDirectory, "objects.raw.bin");
        try
        {
            await using (var stream = new FileStream(evidencePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(RetentionSnapshotStorageGuard.MaximumTotalRetentionBytes);
            }

            var guard = new RetentionSnapshotStorageGuard(
                new SnapshotStorageLayout(root),
                static _ => long.MaxValue);

            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                async () => await guard.EnsureCanStartAsync(CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotStorageLimitReached, exception.ErrorCode);
            Assert.IsTrue(File.Exists(evidencePath));
            Assert.AreEqual(
                RetentionSnapshotStorageGuard.MaximumTotalRetentionBytes,
                new FileInfo(evidencePath).Length);
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
    /// 卷空闲空间恰好覆盖 18,400,000,000 字节捕获峰值和 2 GiB 保留底线时，必须允许开始捕获。
    /// </summary>
    [TestMethod]
    public async Task ReserveCaptureAsync_WhenFreeSpaceExactlyCoversPeakAndFloor_AllowsCapture()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var guard = new RetentionSnapshotStorageGuard(
                new SnapshotStorageLayout(root),
                static _ => 20_547_483_648L);

            using var reservation = await guard.ReserveCaptureAsync(CancellationToken.None);
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
    /// 卷空闲空间比“最坏 raw spool、对象外排中间文件和 2 GiB 保留底线”的并存峰值少一字节时，
    /// 必须在创建 spool 和附加 Profiler 前拒绝捕获。
    /// </summary>
    [TestMethod]
    public async Task ReserveCaptureAsync_WhenFreeSpaceIsOneByteBelowPeakAndFloor_RejectsCapture()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var guard = new RetentionSnapshotStorageGuard(
                new SnapshotStorageLayout(root),
                static _ => 20_547_483_647L);

            DiagnosticsException? exception = null;
            try
            {
                using var unexpectedReservation = await guard.ReserveCaptureAsync(CancellationToken.None);
            }
            catch (DiagnosticsException caught)
            {
                exception = caught;
            }

            Assert.IsNotNull(exception, "少于受保护峰值一字节时必须拒绝捕获租约。");
            Assert.AreEqual(DiagnosticsErrorCode.SnapshotStorageLimitReached, exception.ErrorCode);
            CollectionAssert.AreEqual(
                Array.Empty<string>(),
                Directory.GetDirectories(root, ".retention-raw*", SearchOption.AllDirectories));
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
    /// 已有最终保留文件只剩不足一个最大快照的总配额时，必须在捕获前拒绝，不能等临时文件写完才失败。
    /// </summary>
    [TestMethod]
    public async Task ReserveCaptureAsync_WhenFinalFamilyCannotFitMaximumSnapshot_RejectsCapture()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var existingPath = Path.Combine(root, "existing.retentionheap");
        try
        {
            await using (var stream = new FileStream(existingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(8_589_934_529L);
            }

            var guard = new RetentionSnapshotStorageGuard(
                new SnapshotStorageLayout(root),
                static _ => long.MaxValue);

            DiagnosticsException? exception = null;
            try
            {
                using var unexpectedReservation = await guard.ReserveCaptureAsync(CancellationToken.None);
            }
            catch (DiagnosticsException caught)
            {
                exception = caught;
            }

            Assert.IsNotNull(exception, "最终快照族没有完整最大快照空间时必须拒绝捕获租约。");
            Assert.AreEqual(DiagnosticsErrorCode.SnapshotStorageLimitReached, exception.ErrorCode);
            Assert.AreEqual(8_589_934_529L, new FileInfo(existingPath).Length);
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
    /// 捕获开始后卷空间跌破保留底线时，最终容量复核必须稳定失败，释放 raw spool 后仍保留原始证据。
    /// </summary>
    [TestMethod]
    public async Task CaptureReservation_WhenPostStartCheckFails_PreservesRawEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var availableBytes = 20_547_483_648L;
        RetentionProfilerRawCaptureSpool? spool = null;
        try
        {
            var guard = new RetentionSnapshotStorageGuard(
                new SnapshotStorageLayout(root),
                _ => availableBytes);
            using var reservation = await guard.ReserveCaptureAsync(CancellationToken.None);
            spool = await RetentionProfilerRawCaptureSpool.CreateAsync(
                root,
                new RetentionProfilerRawCapture(
                    [new RetentionProfilerRawObject((nuint)1, (nuint)2, (nuint)24)],
                    [],
                    [],
                    [],
                    []),
                CancellationToken.None);
            var temporaryPath = Path.Combine(root, "capture.tmp.retentionheap");
            await File.WriteAllBytesAsync(temporaryPath, [1]);
            availableBytes = 2_147_483_647L;

            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                async () => await reservation.EnsureCanStoreAsync(temporaryPath, CancellationToken.None));
            spool.Dispose();
            spool = null;

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotStorageLimitReached, exception.ErrorCode);
            var evidenceDirectories = Directory.GetDirectories(root, ".retention-raw-evidence.*");
            Assert.HasCount(1, evidenceDirectories);
            Assert.AreEqual(
                RetentionProfilerRawCaptureSpool.ObjectRecordBytes,
                new FileInfo(Path.Combine(evidenceDirectories[0], "objects.raw.bin")).Length);
        }
        finally
        {
            spool?.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
