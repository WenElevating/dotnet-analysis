using System.Collections.Concurrent;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

public sealed class ImportedSnapshotCatalog
{
    private readonly ConcurrentDictionary<MemorySnapshotId, string> _paths = new();

    public void Register(MemorySnapshotId id, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _paths[id] = Path.GetFullPath(path);
    }

    public bool TryResolve(MemorySnapshotId id, out string path) => _paths.TryGetValue(id, out path!);

    public void Remove(MemorySnapshotId id) => _paths.TryRemove(id, out _);
}
