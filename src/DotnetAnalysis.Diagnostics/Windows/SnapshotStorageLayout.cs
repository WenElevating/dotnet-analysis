using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

public sealed class SnapshotStorageLayout
{
    public SnapshotStorageLayout(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Snapshot root is required.", nameof(rootDirectory));
        }

        RootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(RootDirectory);
    }

    public string RootDirectory { get; }

    public string GetSnapshotDirectory(MemorySnapshotId snapshotId) =>
        Path.Combine(RootDirectory, snapshotId.ToString());

    public string TemporaryPath(Guid id) => Path.Combine(RootDirectory, $".{id:N}.tmp.gcdump");

    public string VisiblePath(Guid id) => Path.Combine(RootDirectory, $"{id:N}.gcdump");

    public string GetTemporaryDumpPath(MemorySnapshotId snapshotId) =>
        Path.Combine(GetSnapshotDirectory(snapshotId), "capture.tmp.gcdump");

    public string GetFinalDumpPath(MemorySnapshotId snapshotId) =>
        Path.Combine(GetSnapshotDirectory(snapshotId), $"{snapshotId}.gcdump");

    public string GetTemporaryManifestPath(MemorySnapshotId snapshotId) =>
        Path.Combine(GetSnapshotDirectory(snapshotId), "capture.tmp.json");

    public string GetFinalManifestPath(MemorySnapshotId snapshotId) =>
        Path.Combine(GetSnapshotDirectory(snapshotId), "snapshot.json");
}
