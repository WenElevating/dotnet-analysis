using System.Diagnostics.CodeAnalysis;
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
}
