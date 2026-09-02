using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Application.Snapshots;

public sealed class MemorySnapshotAnalysisService : IMemorySnapshotAnalysisService
{
    public Task<MemorySnapshotAnalysis> AnalyzeAsync(
        MemorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        throw new DiagnosticsException(
            DiagnosticsErrorCode.SnapshotFormatNotSupported,
            "No memory snapshot reader has been registered.");
    }

    public Task<IReadOnlyList<MemoryObjectInfo>> GetObjectsAsync(
        MemorySnapshot snapshot,
        TypeIdentity type,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(type);
        cancellationToken.ThrowIfCancellationRequested();
        throw new DiagnosticsException(
            DiagnosticsErrorCode.SnapshotFormatNotSupported,
            "No memory snapshot reader has been registered.");
    }

    public Task<MemoryReferencePath?> GetReferencePathAsync(
        MemorySnapshot snapshot,
        ulong objectAddress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        throw new DiagnosticsException(
            DiagnosticsErrorCode.SnapshotFormatNotSupported,
            "No memory snapshot reader has been registered.");
    }
}
