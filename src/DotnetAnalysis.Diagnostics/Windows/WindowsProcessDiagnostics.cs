using System.Diagnostics;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

public sealed class WindowsProcessDiagnostics : IProcessDiagnostics
{
    private readonly ProcessEnumerator _enumerator;
    private readonly ProcessIdentityValidator _identityValidator;
    private readonly RuntimeCapabilitiesResolver _capabilitiesResolver;
    private readonly IProcessMemoryReader _processMemoryReader;
    private readonly ImportedSnapshotCatalog _importedSnapshots;

    public WindowsProcessDiagnostics(
        ProcessEnumerator? enumerator = null,
        ProcessIdentityValidator? identityValidator = null,
        RuntimeCapabilitiesResolver? capabilitiesResolver = null,
        IProcessMemoryReader? processMemoryReader = null,
        ImportedSnapshotCatalog? importedSnapshots = null)
    {
        _enumerator = enumerator ?? new ProcessEnumerator();
        _identityValidator = identityValidator ?? new ProcessIdentityValidator();
        _capabilitiesResolver = capabilitiesResolver ?? new RuntimeCapabilitiesResolver();
        _processMemoryReader = processMemoryReader ?? new ProcessMemoryReader();
        _importedSnapshots = importedSnapshots ?? new ImportedSnapshotCatalog();
    }

    public Task<IReadOnlyList<TargetProcess>> GetProcessesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_enumerator.Enumerate());
    }

    public Task<IProcessDiagnosticsSession> AttachAsync(
        TargetProcess process,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        cancellationToken.ThrowIfCancellationRequested();
        return AttachCoreAsync(process, cancellationToken);
    }

    private async Task<IProcessDiagnosticsSession> AttachCoreAsync(TargetProcess process, CancellationToken cancellationToken)
    {
        await _identityValidator.ValidateAsync(process, cancellationToken).ConfigureAwait(false);
        await _capabilitiesResolver.ValidateAsync(process, cancellationToken).ConfigureAwait(false);
        var sampler = new ProcessMemorySampler(process, _processMemoryReader);
        return new ProcessDiagnosticsSession(process, sampler);
    }

    public Task<MemorySnapshot> OpenSnapshotAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(Path.GetExtension(filePath), ".gcdump", StringComparison.OrdinalIgnoreCase))
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.SnapshotFormatNotSupported,
                "Only .gcdump snapshots are accepted by the diagnostics contract.");
        }

        if (!File.Exists(filePath))
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.CaptureFailed,
                "The snapshot file does not exist.");
        }

        var lastWriteUtc = File.GetLastWriteTimeUtc(filePath);
        var snapshot = new MemorySnapshot(
            MemorySnapshotId.New(),
            MemorySnapshotOrigin.Imported,
            DateTimeOffset.UtcNow,
            null,
            new DateTimeOffset(lastWriteUtc),
            MemorySnapshotState.Analyzing);
        _importedSnapshots.Register(snapshot.Id, filePath);
        return Task.FromResult(snapshot);
    }

    private static bool TryCreateTargetProcess(Process process, out TargetProcess targetProcess)
    {
        targetProcess = null!;
        DateTimeOffset startedAtUtc;
        try
        {
            startedAtUtc = process.StartTime.ToUniversalTime();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            return false;
        }

        string? executablePath = null;
        try
        {
            executablePath = process.MainModule?.FileName;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
        }

        try
        {
            targetProcess = new TargetProcess(
                process.Id,
                startedAtUtc,
                process.ProcessName,
                executablePath);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class UnsupportedProcessDiagnosticsSession(TargetProcess process) : IProcessDiagnosticsSession
    {
        public ProcessDiagnosticsSessionId Id { get; } = ProcessDiagnosticsSessionId.New();

        public TargetProcess Process { get; } = process;

        public ProcessDiagnosticsSessionState State => ProcessDiagnosticsSessionState.Monitoring;

        public Task EndAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<MemoryUsageSample> GetMemoryUsageAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new MemoryUsageSample(
                DateTimeOffset.UtcNow,
                null,
                null,
                MemoryUsageSampleState.Unavailable);
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public Task<MemorySnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new DiagnosticsException(
                DiagnosticsErrorCode.RuntimeNotSupported,
                "Live snapshot capture is not implemented in this adapter yet.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
