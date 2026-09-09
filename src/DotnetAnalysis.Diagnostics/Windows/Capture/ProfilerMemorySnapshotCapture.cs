using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Diagnostics.Windows.Capture;

/// <summary>
/// 使用 CLR Native Profiler 捕获对象图和栈根函数证据的专用保留分析实现。
/// </summary>
/// <remarks>
/// 此捕获器不会回退到 GCDump：调用方选择 <see cref="MemorySnapshotCaptureMode.RetentionAnalysis"/> 即表示
/// 需要真实 GC 根证据。附加完成后由 Profiler 在一次 GC 中写入共享内存，诊断进程负责校验、转换和原子提升文件。
/// </remarks>
internal sealed class ProfilerMemorySnapshotCapture : IMemorySnapshotCapture
{
    private static readonly IReadOnlySet<MemorySnapshotCaptureMode> s_supportedCaptureModes =
        new HashSet<MemorySnapshotCaptureMode> { MemorySnapshotCaptureMode.RetentionAnalysis };
    private static readonly TimeSpan s_attachTimeout = TimeSpan.FromSeconds(15);
    private readonly ProcessIdentityValidator _identityValidator;
    private readonly SnapshotStorageLayout _snapshotLayout;
    private readonly MemorySnapshotStore _snapshotStore;
    private readonly IRetentionSnapshotStorageGuard _storageGuard;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 创建保留分析 Profiler 捕获所需的身份、存储、原生部署和时间依赖。
    /// </summary>
    public ProfilerMemorySnapshotCapture(
        ProcessIdentityValidator identityValidator,
        SnapshotStorageLayout snapshotLayout,
        MemorySnapshotStore snapshotStore,
        IRetentionSnapshotStorageGuard storageGuard,
        TimeProvider timeProvider)
    {
        _identityValidator = identityValidator ?? throw new ArgumentNullException(nameof(identityValidator));
        _snapshotLayout = snapshotLayout ?? throw new ArgumentNullException(nameof(snapshotLayout));
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        _storageGuard = storageGuard ?? throw new ArgumentNullException(nameof(storageGuard));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public IReadOnlySet<MemorySnapshotCaptureMode> SupportedCaptureModes => s_supportedCaptureModes;

    /// <inheritdoc />
    public async Task<MemorySnapshot> CaptureAsync(
        MemorySnapshotCaptureMode captureMode,
        TargetProcess target,
        AllocationSamplingSession allocationCollector,
        CancellationToken cancellationToken)
    {
        if (captureMode is not MemorySnapshotCaptureMode.RetentionAnalysis)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.ProfilerCaptureFailed,
                "保留分析 Profiler 捕获器只支持 RetentionAnalysis 方式。");
        }

        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(allocationCollector);
        await _identityValidator.ValidateAsync(target, cancellationToken).ConfigureAwait(false);
        await _storageGuard.EnsureCanStartAsync(cancellationToken).ConfigureAwait(false);

        var snapshotId = MemorySnapshotId.New();
        var requestedAtUtc = _timeProvider.GetUtcNow();
        var captureStartedAtUtc = _timeProvider.GetUtcNow();
        var snapshotDirectory = _snapshotLayout.GetSnapshotDirectory(snapshotId);
        var temporaryPath = _snapshotLayout.GetTemporaryRetentionHeapPath(snapshotId);
        Directory.CreateDirectory(snapshotDirectory);
        try
        {
            var artifacts = ProfilerNativeArtifactLocator.Resolve();
            using var profilerSession = new RetentionProfilerCaptureSession();
            var attachResult = await ProfilerControllerClient.AttachAsync(
                target.ProcessId,
                s_attachTimeout,
                artifacts.ControllerPath,
                artifacts.ProfilerPath,
                profilerSession.CreateAttachData(),
                cancellationToken).ConfigureAwait(false);
            if (attachResult < 0)
            {
                throw new DiagnosticsException(
                    DiagnosticsErrorCode.ProfilerAttachUnavailable,
                    $"CLR 拒绝附加保留分析 Profiler (HRESULT 0x{attachResult:X8})。");
            }

            var rawCapture = await profilerSession.WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);
            await profilerSession.WaitForDetachAsync(cancellationToken).ConfigureAwait(false);
            var snapshotData = RetentionProfilerSnapshotConverter.Convert(rawCapture);
            await RetentionHeapSnapshot.WriteAsync(temporaryPath, snapshotData, cancellationToken).ConfigureAwait(false);
            var capturedAtUtc = _timeProvider.GetUtcNow();
            var allocationProfile = allocationCollector.Seal(capturedAtUtc);
            var snapshot = new MemorySnapshot(
                snapshotId,
                MemorySnapshotOrigin.Captured,
                requestedAtUtc,
                captureStartedAtUtc,
                capturedAtUtc,
                MemorySnapshotState.Analyzing);
            await _snapshotStore.PromoteRetentionAsync(
                snapshot,
                temporaryPath,
                allocationProfile,
                cancellationToken).ConfigureAwait(false);
            allocationCollector.BeginNextInterval(capturedAtUtc);
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            await CleanupTemporaryCaptureArtifactsAsync(temporaryPath, snapshotDirectory).ConfigureAwait(false);
            throw;
        }
        catch (DiagnosticsException)
        {
            await CleanupTemporaryCaptureArtifactsAsync(temporaryPath, snapshotDirectory).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await CleanupTemporaryCaptureArtifactsAsync(temporaryPath, snapshotDirectory).ConfigureAwait(false);
            throw new DiagnosticsException(
                DiagnosticsErrorCode.ProfilerCaptureFailed,
                "保留分析 Profiler 捕获失败。",
                exception);
        }
    }

    /// <summary>
    /// 清理取消或失败捕获遗留的临时文件和空快照目录；已提升的证据目录不会进入此路径。
    /// </summary>
    private static async Task CleanupTemporaryCaptureArtifactsAsync(string temporaryPath, string snapshotDirectory)
    {
        await MemorySnapshotStore.DeleteTemporaryAsync(temporaryPath).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(snapshotDirectory)
                && !Directory.EnumerateFileSystemEntries(snapshotDirectory).Any())
            {
                Directory.Delete(snapshotDirectory);
            }
        }
        catch (IOException)
        {
            // 临时目录清理由捕获失败的原始错误路径兜底；不应覆盖该诊断结果。
        }
        catch (UnauthorizedAccessException)
        {
            // 临时目录清理由捕获失败的原始错误路径兜底；不应覆盖该诊断结果。
        }
    }
}
