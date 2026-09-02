using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

public sealed class GCDumpSnapshotReader : IMemorySnapshotReader
{
    public bool CanRead(string filePath) => string.Equals(Path.GetExtension(filePath), ".gcdump", StringComparison.OrdinalIgnoreCase);

    public Task<IReadOnlyList<MemoryTypeSummary>> ReadTypeSummariesAsync(string filePath, CancellationToken cancellationToken)
    {
        Validate(filePath, cancellationToken);
        return Task.FromResult<IReadOnlyList<MemoryTypeSummary>>(Array.Empty<MemoryTypeSummary>());
    }

    public Task<IReadOnlyList<MemoryObjectInfo>> ReadObjectsAsync(string filePath, TypeIdentity type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(type);
        Validate(filePath, cancellationToken);
        return Task.FromResult<IReadOnlyList<MemoryObjectInfo>>(Array.Empty<MemoryObjectInfo>());
    }

    public Task<MemoryReferencePath?> ReadReferencePathAsync(string filePath, ulong objectAddress, CancellationToken cancellationToken)
    {
        Validate(filePath, cancellationToken);
        return Task.FromResult<MemoryReferencePath?>(null);
    }

    private static void Validate(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.CaptureFailed, "Snapshot file does not exist.");
        }
    }
}
