using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Tests.Diagnostics;

/// <summary>
/// 验证临时堆工件清理不会改写索引发布已经产生的稳定主结果。
/// </summary>
[TestClass]
public sealed class HeapTemporaryArtifactCleanupTests
{
    /// <summary>
    /// 临时映射文件创建后的主打开失败必须保持原异常，即使另一个句柄让失败清理无法立即删除文件。
    /// </summary>
    [TestMethod]
    public void CreateFileAndOpenWhenCleanupIsLockedPreservesPrimaryFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.HeapOpenCleanup.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "locked.tmp");
        FileStream? blocker = null;
        var expected = new InvalidOperationException("primary open failure");
        try
        {
            var actual = Assert.ThrowsExactly<InvalidOperationException>(() =>
                HeapTemporaryArtifactCleanup.CreateFileAndOpen<object>(
                    path,
                    4096,
                    () =>
                    {
                        blocker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        throw expected;
                    }));

            Assert.AreSame(expected, actual);
            Assert.IsTrue(File.Exists(path));
        }
        finally
        {
            blocker?.Dispose();
            HeapTemporaryArtifactCleanup.TryDeleteFile(path);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 被占用的临时文件不能让 finally 抛错；锁释放后调用方仍可继续回收。
    /// </summary>
    [TestMethod]
    public void TryDeleteFileWhenLockedDoesNotThrowOrLoseRetryability()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.HeapFileCleanup.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "locked.tmp");
        try
        {
            using (var blocker = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                HeapTemporaryArtifactCleanup.TryDeleteFile(path);
                Assert.IsTrue(File.Exists(path));
            }

            HeapTemporaryArtifactCleanup.TryDeleteFile(path);
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
    /// 恢复扫描只删除达到年龄阈值的调用专属目录；锁定遗留项在锁释放后的下一次扫描回收。
    /// </summary>
    [TestMethod]
    public void TryDeleteAbandonedDirectoriesRespectsAgeAndRetriesLockedDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.HeapDirectoryRecovery.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var recent = Path.Combine(root, ".heap-work.recent.tmp");
        var stale = Path.Combine(root, ".heap-work.stale.tmp");
        var locked = Path.Combine(root, ".heap-work.locked.tmp");
        Directory.CreateDirectory(recent);
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(stale, "data.bin"), "stale");
        var lockedPath = Path.Combine(locked, "data.bin");
        FileStream? blocker = null;
        try
        {
            blocker = new FileStream(lockedPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            var staleTimestamp = DateTime.UtcNow - TimeSpan.FromDays(2);
            Directory.SetLastWriteTimeUtc(stale, staleTimestamp);
            Directory.SetLastWriteTimeUtc(locked, staleTimestamp);

            HeapTemporaryArtifactCleanup.TryDeleteAbandonedDirectories(
                root,
                ".heap-work.*.tmp",
                TimeSpan.FromDays(1));

            Assert.IsTrue(Directory.Exists(recent));
            Assert.IsFalse(Directory.Exists(stale));
            Assert.IsTrue(Directory.Exists(locked));
            blocker.Dispose();
            blocker = null;

            HeapTemporaryArtifactCleanup.TryDeleteAbandonedDirectories(
                root,
                ".heap-work.*.tmp",
                TimeSpan.FromDays(1));

            Assert.IsFalse(Directory.Exists(locked));
        }
        finally
        {
            blocker?.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 写入器失败且临时文件仍被占用时，发布器必须保留原始诊断错误而不是抛出清理 IO 异常。
    /// </summary>
    [TestMethod]
    public async Task PublishPreservesPrimaryFailureWhenTemporaryDirectoryIsLocked()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.HeapCleanup.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        FileStream? blocker = null;
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var directory = layout.GetHeapIndexDirectory(MemorySnapshotId.New());

            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                () => HeapIndexArtifactStore.PublishAsync(
                    directory,
                    async (temporaryDirectory, _) =>
                    {
                        blocker = new FileStream(
                            Path.Combine(temporaryDirectory, "cleanup-blocker.bin"),
                            FileMode.CreateNew,
                            FileAccess.ReadWrite,
                            FileShare.None);
                        await Task.Yield();
                        throw new DiagnosticsException(
                            DiagnosticsErrorCode.SnapshotIndexBuildFailed,
                            "primary index failure");
                    },
                    estimatedPeakAdditionalBytes: 0,
                    CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.SnapshotIndexBuildFailed, exception.ErrorCode);
            Assert.AreEqual("primary index failure", exception.Message);
        }
        finally
        {
            blocker?.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
