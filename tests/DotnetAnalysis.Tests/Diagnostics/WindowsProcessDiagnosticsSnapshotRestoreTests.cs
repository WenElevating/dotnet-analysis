using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace DotnetAnalysis.Tests.Diagnostics;

/// <summary>
/// 验证诊断公开边界可以重新打开由本应用持久化的不同快照格式。
/// </summary>
[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "测试名描述行为。")]
public sealed class WindowsProcessDiagnosticsSnapshotRestoreTests
{
    /// <summary>
    /// 已提升且带有 v2 清单的保留分析快照，在新建诊断门面后必须通过公开打开接口恢复。
    /// </summary>
    [TestMethod]
    public async Task OpenSnapshotAsync_WhenManagedRetentionHeapHasManifest_RestoresCapturedSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var catalog = new ImportedSnapshotCatalog();
            var store = new MemorySnapshotStore(layout, catalog, new AcceptAnySnapshotValidator());
            var snapshot = new MemorySnapshot(
                MemorySnapshotId.New(),
                MemorySnapshotOrigin.Captured,
                DateTimeOffset.Parse("2026-09-09T00:00:00Z", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2026-09-09T00:00:01Z", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2026-09-09T00:00:02Z", CultureInfo.InvariantCulture),
                MemorySnapshotState.Analyzing);
            var temporaryPath = layout.GetTemporaryRetentionHeapPath(snapshot.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);
            await RetentionHeapSnapshot.WriteAsync(
                temporaryPath,
                new RetentionHeapSnapshot.SnapshotData(
                    [new TypeIdentity("Sample.Node", "Sample")],
                    [new RetentionHeapSnapshot.ObjectRecord(1, 0, 16)],
                    [],
                    []),
                CancellationToken.None);
            await store.PromoteRetentionAsync(
                snapshot,
                temporaryPath,
                AllocationProfile.NotAvailable(snapshot.RequestedAtUtc, snapshot.CapturedAtUtc!.Value),
                CancellationToken.None);

            await using var eventBus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
            var diagnostics = new WindowsProcessDiagnostics(
                eventBus,
                TimeProvider.System,
                importedSnapshots: new ImportedSnapshotCatalog(),
                snapshotLayout: layout);

            var restored = await diagnostics.OpenSnapshotAsync(
                layout.GetFinalRetentionHeapPath(snapshot.Id),
                CancellationToken.None);

            Assert.AreEqual(snapshot, restored);
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
    /// 将可读性校验替换为无条件成功，隔离存储清单恢复行为。
    /// </summary>
    private sealed class AcceptAnySnapshotValidator : ISnapshotReadabilityValidator
    {
        /// <inheritdoc />
        public Task ValidateAsync(string filePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
