using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotnetAnalysis.Tests.Diagnostics;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class MemorySnapshotStoreTests
{
    /// <summary>
    /// 清单移动失败时只能回滚本次新建的最终快照；阻塞清单移除后必须允许使用新临时证据重试。
    /// </summary>
    [TestMethod]
    public async Task PromoteAsync_WhenManifestMoveFails_RollsBackOwnedSnapshotAndAllowsRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var store = new MemorySnapshotStore(layout, new ImportedSnapshotCatalog(), new AcceptAnySnapshotValidator());
            var snapshot = new MemorySnapshot(
                MemorySnapshotId.New(),
                MemorySnapshotOrigin.Captured,
                DateTimeOffset.Parse("2026-09-10T02:00:00Z", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2026-09-10T02:00:01Z", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2026-09-10T02:00:02Z", CultureInfo.InvariantCulture),
                MemorySnapshotState.Analyzing);
            var temporaryPath = layout.GetTemporaryDumpPath(snapshot.Id);
            var finalSnapshotPath = layout.GetFinalDumpPath(snapshot.Id);
            var finalManifestPath = layout.GetFinalManifestPath(snapshot.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);
            await File.WriteAllBytesAsync(temporaryPath, [1, 2, 3]);
            await File.WriteAllBytesAsync(finalManifestPath, [4, 5, 6]);

            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                async () => await store.PromoteAsync(
                    snapshot,
                    temporaryPath,
                    AllocationProfile.NotAvailable(snapshot.RequestedAtUtc, snapshot.CapturedAtUtc!.Value),
                    CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.CaptureFailed, exception.ErrorCode);
            Assert.IsFalse(File.Exists(finalSnapshotPath));
            CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(finalManifestPath));
            File.Delete(finalManifestPath);
            await File.WriteAllBytesAsync(temporaryPath, [7, 8, 9]);

            await store.PromoteAsync(
                snapshot,
                temporaryPath,
                AllocationProfile.NotAvailable(snapshot.RequestedAtUtc, snapshot.CapturedAtUtc!.Value),
                CancellationToken.None);

            CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(finalSnapshotPath));
            Assert.IsTrue(File.Exists(finalManifestPath));
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
    /// 同一快照标识的重复提升失败时，只能清理本次临时输入，不能删除第一次已发布的快照及清单。
    /// </summary>
    [TestMethod]
    public async Task PromoteAsync_WhenSnapshotAlreadyExists_PreservesPublishedSnapshotAndManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var store = new MemorySnapshotStore(layout, new ImportedSnapshotCatalog(), new AcceptAnySnapshotValidator());
            var snapshot = new MemorySnapshot(
                MemorySnapshotId.New(),
                MemorySnapshotOrigin.Captured,
                DateTimeOffset.Parse("2026-09-10T01:00:00Z", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2026-09-10T01:00:01Z", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2026-09-10T01:00:02Z", CultureInfo.InvariantCulture),
                MemorySnapshotState.Analyzing);
            var profile = AllocationProfile.NotAvailable(snapshot.RequestedAtUtc, snapshot.CapturedAtUtc!.Value);
            var firstTemporaryPath = layout.GetTemporaryDumpPath(snapshot.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(firstTemporaryPath)!);
            await File.WriteAllBytesAsync(firstTemporaryPath, [1, 2, 3]);
            await store.PromoteAsync(snapshot, firstTemporaryPath, profile, CancellationToken.None);
            var finalSnapshotPath = layout.GetFinalDumpPath(snapshot.Id);
            var finalManifestPath = layout.GetFinalManifestPath(snapshot.Id);
            var publishedSnapshot = await File.ReadAllBytesAsync(finalSnapshotPath);
            var publishedManifest = await File.ReadAllBytesAsync(finalManifestPath);
            var duplicateTemporaryPath = Path.Combine(Path.GetDirectoryName(firstTemporaryPath)!, "duplicate.gcdump.tmp");
            await File.WriteAllBytesAsync(duplicateTemporaryPath, [9, 8, 7]);

            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                async () => await store.PromoteAsync(snapshot, duplicateTemporaryPath, profile, CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.CaptureFailed, exception.ErrorCode);
            CollectionAssert.AreEqual(publishedSnapshot, await File.ReadAllBytesAsync(finalSnapshotPath));
            CollectionAssert.AreEqual(publishedManifest, await File.ReadAllBytesAsync(finalManifestPath));
            Assert.IsFalse(File.Exists(duplicateTemporaryPath));
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
    public async Task PromoteAndRestoreAsync_PreservesCapturedSnapshotMetadataAndAllocationProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var store = new MemorySnapshotStore(layout, new ImportedSnapshotCatalog(), new AcceptAnySnapshotValidator());
            var snapshot = new MemorySnapshot(
                MemorySnapshotId.New(),
                MemorySnapshotOrigin.Captured,
                DateTimeOffset.Parse("2026-09-04T01:00:00Z", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2026-09-04T01:00:01Z", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2026-09-04T01:00:02Z", CultureInfo.InvariantCulture),
                MemorySnapshotState.Analyzing);
            var temporaryPath = layout.GetTemporaryDumpPath(snapshot.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);
            await File.WriteAllBytesAsync(temporaryPath, [1, 2, 3]);
            var profile = new AllocationProfile(
                snapshot.RequestedAtUtc,
                snapshot.CapturedAtUtc!.Value,
                [],
                AllocationProfileDataQuality.Continuous,
                AllocationCallStackQuality.NotAvailable);

            await store.PromoteAsync(snapshot, temporaryPath, profile, CancellationToken.None);
            var restored = await store.TryRestoreAsync(layout.GetFinalDumpPath(snapshot.Id), CancellationToken.None);

            Assert.IsNotNull(restored);
            Assert.AreEqual(snapshot, restored.Snapshot);
            Assert.AreEqual(profile, restored.AllocationProfile);
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
    /// 新写入的受管快照清单必须使用 v2，并显式记录文件格式，以便重开时选择正确读取器。
    /// </summary>
    [TestMethod]
    public async Task PromoteAsync_WritesV2ManifestWithExplicitGcdumpFormat()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var store = new MemorySnapshotStore(layout, new ImportedSnapshotCatalog(), new AcceptAnySnapshotValidator());
            var snapshot = new MemorySnapshot(
                MemorySnapshotId.New(),
                MemorySnapshotOrigin.Captured,
                DateTimeOffset.Parse("2026-09-04T01:00:00Z", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2026-09-04T01:00:01Z", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2026-09-04T01:00:02Z", CultureInfo.InvariantCulture),
                MemorySnapshotState.Analyzing);
            var temporaryPath = layout.GetTemporaryDumpPath(snapshot.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);
            await File.WriteAllBytesAsync(temporaryPath, [1, 2, 3]);

            await store.PromoteAsync(
                snapshot,
                temporaryPath,
                AllocationProfile.NotAvailable(snapshot.RequestedAtUtc, snapshot.CapturedAtUtc!.Value),
                CancellationToken.None);

            await using var stream = File.OpenRead(layout.GetFinalManifestPath(snapshot.Id));
            using var manifest = await JsonDocument.ParseAsync(stream);
            Assert.AreEqual(2, manifest.RootElement.GetProperty("ManifestVersion").GetInt32());
            Assert.AreEqual("GCDump", manifest.RootElement.GetProperty("SnapshotFormat").GetString());
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
    /// 保留分析文件提升后必须保持专用扩展名及 v2 格式标识，防止以后按 GCDump 读取。
    /// </summary>
    [TestMethod]
    public async Task PromoteRetentionAsync_PreservesTheDedicatedRetentionHeapFormat()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var store = new MemorySnapshotStore(layout, new ImportedSnapshotCatalog(), new AcceptAnySnapshotValidator());
            var snapshot = new MemorySnapshot(
                MemorySnapshotId.New(),
                MemorySnapshotOrigin.Captured,
                DateTimeOffset.Parse("2026-09-04T01:00:00Z", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2026-09-04T01:00:01Z", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2026-09-04T01:00:02Z", CultureInfo.InvariantCulture),
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

            var stored = await store.PromoteRetentionAsync(
                snapshot,
                temporaryPath,
                AllocationProfile.NotAvailable(snapshot.RequestedAtUtc, snapshot.CapturedAtUtc!.Value),
                CancellationToken.None);
            var restored = await store.ResolveAsync(snapshot.Id, CancellationToken.None);

            Assert.AreEqual(SnapshotStorageFormat.RetentionHeap, stored.SnapshotFormat);
            Assert.AreEqual(SnapshotStorageFormat.RetentionHeap, restored.SnapshotFormat);
            StringAssert.EndsWith(stored.FilePath, ".retentionheap", StringComparison.OrdinalIgnoreCase);
            Assert.IsTrue(File.Exists(stored.FilePath));
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
    /// 调用方持有捕获级容量租约时，存储层必须复用它完成提升，不能再次获取同一目录锁造成自锁。
    /// </summary>
    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task PromoteRetentionAsync_WhenCaptureReservationIsHeld_ReusesReservation()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var storageGuard = new RetentionSnapshotStorageGuard(layout, static _ => long.MaxValue);
            var store = new MemorySnapshotStore(
                layout,
                new ImportedSnapshotCatalog(),
                new AcceptAnySnapshotValidator(),
                storageGuard);
            var snapshot = new MemorySnapshot(
                MemorySnapshotId.New(),
                MemorySnapshotOrigin.Captured,
                DateTimeOffset.Parse("2026-09-10T03:00:00Z", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2026-09-10T03:00:01Z", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2026-09-10T03:00:02Z", CultureInfo.InvariantCulture),
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

            using var reservation = await storageGuard.ReserveCaptureAsync(CancellationToken.None);
            var stored = await store.PromoteRetentionAsync(
                snapshot,
                temporaryPath,
                AllocationProfile.NotAvailable(snapshot.RequestedAtUtc, snapshot.CapturedAtUtc!.Value),
                reservation,
                CancellationToken.None);

            Assert.IsTrue(File.Exists(stored.FilePath));
            Assert.AreEqual(SnapshotStorageFormat.RetentionHeap, stored.SnapshotFormat);
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
    /// 旧版 v1 GCDump 清单不含格式字段，恢复时必须仍按 GCDump 处理。
    /// </summary>
    [TestMethod]
    public async Task TryRestoreAsync_WhenManifestIsV1_UsesTheLegacyGcdumpFormat()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var store = new MemorySnapshotStore(layout, new ImportedSnapshotCatalog(), new AcceptAnySnapshotValidator());
            var snapshot = new MemorySnapshot(
                MemorySnapshotId.New(),
                MemorySnapshotOrigin.Captured,
                DateTimeOffset.Parse("2026-09-04T01:00:00Z", CultureInfo.InvariantCulture),
                null,
                DateTimeOffset.Parse("2026-09-04T01:00:02Z", CultureInfo.InvariantCulture),
                MemorySnapshotState.Analyzing);
            var temporaryPath = layout.GetTemporaryDumpPath(snapshot.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);
            await File.WriteAllBytesAsync(temporaryPath, [1, 2, 3]);
            await store.PromoteAsync(
                snapshot,
                temporaryPath,
                AllocationProfile.NotAvailable(snapshot.RequestedAtUtc, snapshot.CapturedAtUtc!.Value),
                CancellationToken.None);

            var manifestPath = layout.GetFinalManifestPath(snapshot.Id);
            var node = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            node["ManifestVersion"] = 1;
            _ = node.Remove("SnapshotFormat");
            await File.WriteAllTextAsync(manifestPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var restored = await store.TryRestoreAsync(layout.GetFinalDumpPath(snapshot.Id), CancellationToken.None);

            Assert.IsNotNull(restored);
            Assert.AreEqual(SnapshotStorageFormat.GCDump, restored.SnapshotFormat);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class AcceptAnySnapshotValidator : ISnapshotReadabilityValidator
    {
        public Task ValidateAsync(string filePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
