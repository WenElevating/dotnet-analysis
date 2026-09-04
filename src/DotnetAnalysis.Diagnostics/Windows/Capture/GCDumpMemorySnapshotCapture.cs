using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Diagnostics.Windows.Capture;

/// <summary>
/// 使用 GCDump 捕获、封存分配概要并提升快照文件的内部实现。
/// </summary>
internal sealed class GCDumpMemorySnapshotCapture : IMemorySnapshotCapture
{
    private readonly ProcessIdentityValidator _identityValidator;
    private readonly SnapshotStorageLayout _snapshotLayout;
    private readonly MemorySnapshotStore _snapshotStore;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 创建 GCDump 捕获所需的身份校验、存储和时间依赖。
    /// </summary>
    public GCDumpMemorySnapshotCapture(
        ProcessIdentityValidator identityValidator,
        SnapshotStorageLayout snapshotLayout,
        MemorySnapshotStore snapshotStore,
        TimeProvider timeProvider)
    {
        _identityValidator = identityValidator ?? throw new ArgumentNullException(nameof(identityValidator));
        _snapshotLayout = snapshotLayout ?? throw new ArgumentNullException(nameof(snapshotLayout));
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async Task<MemorySnapshot> CaptureAsync(
        TargetProcess target,
        AllocationSamplingSession allocationCollector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(allocationCollector);

        await _identityValidator.ValidateAsync(target, cancellationToken).ConfigureAwait(false);

        var snapshotId = MemorySnapshotId.New();
        var requestedAtUtc = _timeProvider.GetUtcNow();
        var captureStartedAtUtc = _timeProvider.GetUtcNow();
        var (temporaryPath, capturedAtUtc) = await GCDumpSnapshotCollector.CaptureAsync(
            target,
            _snapshotLayout,
            cancellationToken).ConfigureAwait(false);

        var allocationProfile = allocationCollector.Seal(capturedAtUtc);
        var snapshot = new MemorySnapshot(
            snapshotId,
            MemorySnapshotOrigin.Captured,
            requestedAtUtc,
            captureStartedAtUtc,
            capturedAtUtc,
            MemorySnapshotState.Analyzing);
        await _snapshotStore.PromoteAsync(
            snapshot,
            temporaryPath,
            allocationProfile,
            cancellationToken).ConfigureAwait(false);
        allocationCollector.BeginNextInterval(capturedAtUtc);

        return snapshot;
    }
}
