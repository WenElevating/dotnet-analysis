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
    private readonly SnapshotStorageLayout _snapshotLayout;
    private readonly MemorySnapshotStore _snapshotStore;

    public WindowsProcessDiagnostics(
        ProcessEnumerator? enumerator = null,
        ProcessIdentityValidator? identityValidator = null,
        RuntimeCapabilitiesResolver? capabilitiesResolver = null,
        IProcessMemoryReader? processMemoryReader = null,
        ImportedSnapshotCatalog? importedSnapshots = null,
        SnapshotStorageLayout? snapshotLayout = null)
    {
        _enumerator = enumerator ?? new ProcessEnumerator();
        _identityValidator = identityValidator ?? new ProcessIdentityValidator();
        _capabilitiesResolver = capabilitiesResolver ?? new RuntimeCapabilitiesResolver();
        _processMemoryReader = processMemoryReader ?? new ProcessMemoryReader();
        _importedSnapshots = importedSnapshots ?? new ImportedSnapshotCatalog();
        _snapshotLayout = snapshotLayout ?? new SnapshotStorageLayout(
            Path.Combine(Path.GetTempPath(), "DotnetAnalysis", "Snapshots"));
        _snapshotStore = new MemorySnapshotStore(_snapshotLayout, _importedSnapshots);
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
        var allocationCollector = new AllocationSampleCollector(
            new AllocationProfileBuilder(DateTimeOffset.UtcNow));
        try
        {
            await allocationCollector.StartAsync(process, cancellationToken).ConfigureAwait(false);
        }
        catch (DiagnosticsException)
        {
            // Allocation sampling is an independent timeline.  A runtime may
            // expose memory counters while refusing the allocation provider;
            // preserve the session and mark that interval as interrupted.
            allocationCollector.MarkInterrupted(DateTimeOffset.UtcNow);
        }

        return new ProcessDiagnosticsSession(
            process,
            sampler,
            capture: cancellationToken => CaptureSnapshotCoreAsync(process, allocationCollector, cancellationToken),
            allocationCollector: allocationCollector);
    }

    private async Task<MemorySnapshot> CaptureSnapshotCoreAsync(
        TargetProcess target,
        AllocationSampleCollector allocationCollector,
        CancellationToken cancellationToken)
    {
        await _identityValidator.ValidateAsync(target, cancellationToken).ConfigureAwait(false);
        var snapshotId = MemorySnapshotId.New();
        var requestedAtUtc = DateTimeOffset.UtcNow;
        var captureStartedAtUtc = DateTimeOffset.UtcNow;
        var (temporaryPath, capturedAtUtc) = await GCDumpSnapshotCollector.CaptureAsync(
            target,
            _snapshotLayout,
            cancellationToken).ConfigureAwait(false);

        var allocationProfile = allocationCollector.Seal(capturedAtUtc);
        await _snapshotStore.PromoteAsync(
            snapshotId,
            temporaryPath,
            allocationProfile,
            cancellationToken).ConfigureAwait(false);
        allocationCollector.BeginNextInterval(capturedAtUtc);

        return new MemorySnapshot(
            snapshotId,
            MemorySnapshotOrigin.Captured,
            requestedAtUtc,
            captureStartedAtUtc,
            capturedAtUtc,
            MemorySnapshotState.Analyzing);
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

}
