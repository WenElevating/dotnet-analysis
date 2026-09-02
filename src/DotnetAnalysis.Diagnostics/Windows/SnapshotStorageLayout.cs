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

    public string TemporaryPath(Guid id) => Path.Combine(RootDirectory, $".{id:N}.tmp.gcdump");

    public string VisiblePath(Guid id) => Path.Combine(RootDirectory, $"{id:N}.gcdump");
}
