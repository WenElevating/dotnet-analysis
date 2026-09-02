using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

public sealed class DumpSnapshotReader : IMemorySnapshotReader
{
    public bool CanRead(string filePath) => string.Equals(Path.GetExtension(filePath), ".dmp", StringComparison.OrdinalIgnoreCase);

    public Task<IReadOnlyList<MemoryTypeSummary>> ReadTypeSummariesAsync(string filePath, CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<MemoryTypeSummary>>(Unsupported());

    public Task<IReadOnlyList<MemoryObjectInfo>> ReadObjectsAsync(string filePath, TypeIdentity type, CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<MemoryObjectInfo>>(Unsupported());

    public Task<MemoryReferencePath?> ReadReferencePathAsync(string filePath, ulong objectAddress, CancellationToken cancellationToken) =>
        Task.FromException<MemoryReferencePath?>(Unsupported());

    private static DiagnosticsException Unsupported() =>
        new(DiagnosticsErrorCode.SnapshotFormatNotSupported, "Dump snapshots are not supported yet.");
}
