using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 提供 Windows 进程枚举、附着、采样和快照导入门面。
/// </summary>
public sealed class WindowsProcessDiagnostics : IProcessDiagnostics
{
    private readonly ProcessEnumerator _enumerator;
    private readonly ProcessIdentityValidator _identityValidator;
    private readonly RuntimeCapabilitiesResolver _capabilitiesResolver;
    private readonly IProcessMemoryReader _processMemoryReader;
    private readonly ImportedSnapshotCatalog _importedSnapshots;
    private readonly SnapshotStorageLayout _snapshotLayout;
    private readonly MemorySnapshotStore _snapshotStore;

    /// <summary>
    /// 创建 Windows 进程诊断门面及其身份、运行时、采样和存储依赖。
    /// </summary>
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

    /// <summary>
    /// 枚举当前可附着的进程。
    /// </summary>
    /// <param name="cancellationToken">枚举前检查的取消令牌。</param>
    public Task<IReadOnlyList<TargetProcess>> GetProcessesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_enumerator.Enumerate());
    }

    /// <summary>
    /// 验证目标身份和运行时能力后附着到进程。
    /// </summary>
    /// <param name="process">要附着的目标进程。</param>
    /// <param name="cancellationToken">附着过程的取消令牌。</param>
    public Task<IProcessDiagnosticsSession> AttachAsync(
        TargetProcess process,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        cancellationToken.ThrowIfCancellationRequested();
        return AttachCoreAsync(process, cancellationToken);
    }

    /// <summary>
    /// 完成身份与能力验证后创建采样和捕获会话。
    /// </summary>
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

    /// <summary>
    /// 执行快照捕获、分配概要封存和持久化提升。
    /// </summary>
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

    /// <summary>
    /// 打开现有 .gcdump 文件并创建处于分析中的导入快照。
    /// </summary>
    /// <param name="filePath">现有 .gcdump 文件路径。</param>
    /// <param name="cancellationToken">打开前检查的取消令牌。</param>
    /// <returns>已登记到导入目录的快照描述。</returns>
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
