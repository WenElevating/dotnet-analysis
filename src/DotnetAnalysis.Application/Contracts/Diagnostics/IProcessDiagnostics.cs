using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Application.Contracts.Diagnostics;

public interface IProcessDiagnostics
{
    Task<IReadOnlyList<TargetProcess>> GetProcessesAsync(
        CancellationToken cancellationToken);

    Task<IProcessDiagnosticsSession> AttachAsync(
        TargetProcess process,
        CancellationToken cancellationToken);

    Task<MemorySnapshot> OpenSnapshotAsync(
        string filePath,
        CancellationToken cancellationToken);
}
