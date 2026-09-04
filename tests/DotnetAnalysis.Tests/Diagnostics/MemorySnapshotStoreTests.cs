using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

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

    private sealed class AcceptAnySnapshotValidator : ISnapshotReadabilityValidator
    {
        public Task ValidateAsync(string filePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
