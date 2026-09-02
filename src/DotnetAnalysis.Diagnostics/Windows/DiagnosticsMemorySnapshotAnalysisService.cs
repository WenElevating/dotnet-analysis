using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

public sealed class DiagnosticsMemorySnapshotAnalysisService : IMemorySnapshotAnalysisService
{
    private readonly ImportedSnapshotCatalog _catalog;
    private readonly MemorySnapshotReaderRegistry _registry;

    public DiagnosticsMemorySnapshotAnalysisService(ImportedSnapshotCatalog catalog, MemorySnapshotReaderRegistry registry)
    {
        _catalog = catalog;
        _registry = registry;
    }

    public async Task<MemorySnapshotAnalysis> AnalyzeAsync(MemorySnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_catalog.TryResolve(snapshot.Id, out var path))
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.CaptureFailed, "Snapshot is not registered.");
        }

        var reader = _registry.Resolve(path);
        var types = await reader.ReadTypeSummariesAsync(path, cancellationToken).ConfigureAwait(false);
        var analysisSnapshot = snapshot.State == MemorySnapshotState.Analyzing
            ? snapshot.MoveTo(MemorySnapshotState.Ready)
            : snapshot;
        return new MemorySnapshotAnalysis(
            analysisSnapshot,
            types,
            AllocationProfile.NotAvailable(snapshot.RequestedAtUtc, snapshot.CapturedAtUtc ?? snapshot.RequestedAtUtc));
    }

    public Task<IReadOnlyList<MemoryObjectInfo>> GetObjectsAsync(MemorySnapshot snapshot, TypeIdentity type, CancellationToken cancellationToken)
    {
        if (!_catalog.TryResolve(snapshot.Id, out var path))
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.CaptureFailed, "Snapshot is not registered.");
        }

        return _registry.Resolve(path).ReadObjectsAsync(path, type, cancellationToken);
    }

    public Task<MemoryReferencePath?> GetReferencePathAsync(MemorySnapshot snapshot, ulong objectAddress, CancellationToken cancellationToken)
    {
        if (!_catalog.TryResolve(snapshot.Id, out var path))
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.CaptureFailed, "Snapshot is not registered.");
        }

        return _registry.Resolve(path).ReadReferencePathAsync(path, objectAddress, cancellationToken);
    }
}
