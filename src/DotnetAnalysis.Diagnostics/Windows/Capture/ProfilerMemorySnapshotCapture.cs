using System.Diagnostics;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;
using Microsoft.Win32.SafeHandles;

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
        using var captureStorageReservation = await _storageGuard
            .ReserveCaptureAsync(cancellationToken)
            .ConfigureAwait(false);

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
            await _identityValidator.ValidateAsync(target, cancellationToken).ConfigureAwait(false);
            using var targetProcess = OpenTargetProcess(target.ProcessId);
            using var targetExitWaitHandle = new ProcessExitWaitHandle(targetProcess);
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

            using var rawCaptureSpool = await profilerSession
                .WaitForCompletionToSpoolAsync(snapshotDirectory, targetExitWaitHandle, cancellationToken)
                .ConfigureAwait(false);
            await profilerSession.WaitForDetachAsync(cancellationToken).ConfigureAwait(false);
            await RetentionHeapSnapshot
                .WriteFromProfilerSpoolAsync(temporaryPath, rawCaptureSpool, cancellationToken)
                .ConfigureAwait(false);
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
                captureStorageReservation,
                cancellationToken).ConfigureAwait(false);
            rawCaptureSpool.DeleteAfterSuccessfulPublication();
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
    /// 打开并固定目标进程实例的等待句柄；受保护进程保留访问拒绝，其余不可用状态映射为目标退出。
    /// </summary>
    /// <param name="processId">即将附加的目标进程 PID。</param>
    /// <returns>必须由调用方持有到 Profiler 捕获结束的进程对象。</returns>
    /// <exception cref="DiagnosticsException">目标已退出或当前进程无权打开等待句柄时引发。</exception>
    private static Process OpenTargetProcess(int processId)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            _ = process.SafeHandle;
            return process;
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 5)
        {
            process?.Dispose();
            throw new DiagnosticsException(
                DiagnosticsErrorCode.AccessDenied,
                "无法打开目标进程退出等待句柄。",
                exception);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            process?.Dispose();
            throw DiagnosticsExceptionFactory.TargetExited(exception);
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

    /// <summary>
    /// 将由 <see cref="Process"/> 拥有的可等待进程句柄投影为不接管所有权的 <see cref="WaitHandle"/>。
    /// </summary>
    private sealed class ProcessExitWaitHandle : WaitHandle
    {
        /// <summary>
        /// 创建与指定进程实例绑定的退出等待句柄；调用方必须让进程对象活到本实例释放之后。
        /// </summary>
        /// <param name="process">持有实际操作系统进程句柄的进程对象。</param>
        public ProcessExitWaitHandle(Process process)
        {
            ArgumentNullException.ThrowIfNull(process);
            SafeWaitHandle = new SafeWaitHandle(process.SafeHandle.DangerousGetHandle(), ownsHandle: false);
        }
    }
}
