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
