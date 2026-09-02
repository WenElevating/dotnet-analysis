using System.Collections.Concurrent;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

public sealed record StoredSnapshot(
    MemorySnapshotId SnapshotId,
    string FilePath,
    AllocationProfile AllocationProfile);

public sealed class MemorySnapshotStore
{
    private readonly SnapshotStorageLayout _layout;
    private readonly ConcurrentDictionary<MemorySnapshotId, StoredSnapshot> _snapshots = new();

    public MemorySnapshotStore(SnapshotStorageLayout layout)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    public async Task<StoredSnapshot> PromoteAsync(
        MemorySnapshotId snapshotId,
        string temporaryPath,
        AllocationProfile allocationProfile,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentNullException.ThrowIfNull(allocationProfile);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!File.Exists(temporaryPath))
            {
                throw new IOException("Temporary snapshot does not exist.");
            }

            await using (var stream = File.OpenRead(temporaryPath))
            {
                if (stream.Length == 0)
                {
                    throw new IOException("Temporary snapshot is empty.");
                }
            }

            var visiblePath = _layout.VisiblePath(snapshotId.Value);
            File.Move(temporaryPath, visiblePath, true);
            var stored = new StoredSnapshot(snapshotId, visiblePath, allocationProfile);
            _snapshots[snapshotId] = stored;
            return stored;
        }
        catch (DiagnosticsException)
        {
            await DeleteTemporaryAsync(temporaryPath).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await DeleteTemporaryAsync(temporaryPath).ConfigureAwait(false);
            throw new DiagnosticsException(DiagnosticsErrorCode.CaptureFailed, "Snapshot promotion failed.", exception);
        }
    }

    public static Task DeleteTemporaryAsync(string temporaryPath)
    {
        if (!string.IsNullOrWhiteSpace(temporaryPath))
        {
            TryDelete(temporaryPath);
            TryDelete(temporaryPath + ".manifest");
        }

        return Task.CompletedTask;
    }

    public Task<StoredSnapshot> ResolveAsync(MemorySnapshotId snapshotId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _snapshots.TryGetValue(snapshotId, out var snapshot)
            ? Task.FromResult(snapshot)
            : Task.FromException<StoredSnapshot>(new DiagnosticsException(
                DiagnosticsErrorCode.CaptureFailed,
                "Snapshot is not available."));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
