using DotnetAnalysis.Application.Contracts.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

public sealed class MemorySnapshotReaderRegistry
{
    private readonly IReadOnlyList<IMemorySnapshotReader> _readers;

    public MemorySnapshotReaderRegistry(IEnumerable<IMemorySnapshotReader> readers)
    {
        _readers = readers?.ToArray() ?? throw new ArgumentNullException(nameof(readers));
    }

    public IMemorySnapshotReader Resolve(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.CaptureFailed, "Snapshot file does not exist.");
        }

        var reader = _readers.SingleOrDefault(candidate => candidate.CanRead(filePath));
        return reader ?? throw new DiagnosticsException(
            DiagnosticsErrorCode.SnapshotFormatNotSupported,
            "No reader supports the snapshot format.");
    }
}
