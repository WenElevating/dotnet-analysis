using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

internal sealed class DiagnosticsMemorySnapshotAnalysisService : IMemorySnapshotAnalysisService
{
    private readonly ImportedSnapshotCatalog _catalog;
    private readonly MemorySnapshotStore _store;
    private readonly MemorySnapshotReaderRegistry _registry;

    public DiagnosticsMemorySnapshotAnalysisService(
        ImportedSnapshotCatalog catalog,
        MemorySnapshotStore store,
        MemorySnapshotReaderRegistry registry)
    {
        _catalog = catalog;
        _store = store;
        _registry = registry;
    }

    public async Task<MemorySnapshotAnalysis> AnalyzeAsync(MemorySnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var path = await ResolvePathAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);

        var reader = _registry.Resolve(path);
        var types = await reader.ReadTypeSummariesAsync(path, cancellationToken).ConfigureAwait(false);
        var endedAtUtc = snapshot.CapturedAtUtc ?? snapshot.RequestedAtUtc;
        if (endedAtUtc < snapshot.RequestedAtUtc)
        {
            // Imported files can carry a filesystem timestamp earlier than
            // the moment the user opened them.  The Core contract requires a
            // non-decreasing interval, so clamp the quality-only interval.
            endedAtUtc = snapshot.RequestedAtUtc;
        }

        var allocationProfile = AllocationProfile.NotAvailable(snapshot.RequestedAtUtc, endedAtUtc);
        if (snapshot.Origin == MemorySnapshotOrigin.Captured)
        {
            var stored = await _store.ResolveAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);
            allocationProfile = stored.AllocationProfile;
        }

        return new MemorySnapshotAnalysis(
            snapshot.State == MemorySnapshotState.Analyzing
                ? snapshot.MoveTo(MemorySnapshotState.Ready)
                : snapshot,
            types,
            allocationProfile);
    }

    public Task<IReadOnlyList<MemoryObjectInfo>> GetObjectsAsync(MemorySnapshot snapshot, TypeIdentity type, CancellationToken cancellationToken)
    {
        return ReadObjectsCoreAsync(snapshot, type, cancellationToken);
    }

    public Task<MemoryReferencePath?> GetReferencePathAsync(MemorySnapshot snapshot, ulong objectAddress, CancellationToken cancellationToken)
    {
        return ReadReferencePathCoreAsync(snapshot, objectAddress, cancellationToken);
    }

    private async Task<string> ResolvePathAsync(MemorySnapshotId snapshotId, CancellationToken cancellationToken)
    {
        if (_catalog.TryResolve(snapshotId, out var path))
        {
            return path;
        }

        var resolved = await _store.ResolveAsync(snapshotId, cancellationToken).ConfigureAwait(false);
        _catalog.Register(snapshotId, resolved.FilePath);
        return resolved.FilePath;
    }

    private async Task<IReadOnlyList<MemoryObjectInfo>> ReadObjectsCoreAsync(
        MemorySnapshot snapshot,
        TypeIdentity type,
        CancellationToken cancellationToken)
    {
        var path = await ResolvePathAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);
        return await _registry.Resolve(path).ReadObjectsAsync(path, type, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MemoryReferencePath?> ReadReferencePathCoreAsync(
        MemorySnapshot snapshot,
        ulong objectAddress,
        CancellationToken cancellationToken)
    {
        var path = await ResolvePathAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);
        return await _registry.Resolve(path).ReadReferencePathAsync(path, objectAddress, cancellationToken).ConfigureAwait(false);
    }
}
