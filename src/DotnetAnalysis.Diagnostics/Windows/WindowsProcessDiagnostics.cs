using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows.Capture;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly IMemorySnapshotCapture _snapshotCapture;
    private readonly IEventBus _eventBus;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProcessDiagnosticsSession> _sessionLogger;

    internal IEventBus EventBus => _eventBus;

    /// <summary>
    /// 创建 Windows 进程诊断门面及其身份、运行时、采样和存储依赖。
    /// </summary>
    public WindowsProcessDiagnostics(
        IEventBus eventBus,
        TimeProvider timeProvider,
        ILogger<ProcessDiagnosticsSession>? sessionLogger = null,
        ProcessEnumerator? enumerator = null,
        ProcessIdentityValidator? identityValidator = null,
        RuntimeCapabilitiesResolver? capabilitiesResolver = null,
        IProcessMemoryReader? processMemoryReader = null,
        ImportedSnapshotCatalog? importedSnapshots = null,
        SnapshotStorageLayout? snapshotLayout = null)
    {
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _sessionLogger = sessionLogger ?? NullLogger<ProcessDiagnosticsSession>.Instance;
        _enumerator = enumerator ?? new ProcessEnumerator();
        _identityValidator = identityValidator ?? new ProcessIdentityValidator();
        _capabilitiesResolver = capabilitiesResolver ?? new RuntimeCapabilitiesResolver();
        _processMemoryReader = processMemoryReader ?? new ProcessMemoryReader();
        _importedSnapshots = importedSnapshots ?? new ImportedSnapshotCatalog();
        _snapshotLayout = snapshotLayout ?? new SnapshotStorageLayout(
            SnapshotStorageLayout.GetDefaultRootDirectory());
        _snapshotStore = new MemorySnapshotStore(_snapshotLayout, _importedSnapshots);
        _snapshotCapture = new GCDumpMemorySnapshotCapture(
            _identityValidator,
            _snapshotLayout,
            _snapshotStore,
            _timeProvider);
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
        var allocationCollector = new AllocationSamplingSession(
            new AllocationProfileBuilder(_timeProvider.GetUtcNow()));
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
            allocationCollector,
            _snapshotCapture,
            _eventBus,
            _timeProvider,
            _sessionLogger);
    }

    /// <summary>
    /// 打开现有 .gcdump 文件并创建处于分析中的导入快照。
    /// </summary>
    /// <param name="filePath">现有 .gcdump 文件路径。</param>
    /// <param name="cancellationToken">打开前检查的取消令牌。</param>
    /// <returns>已登记到导入目录的快照描述。</returns>
    public async Task<MemorySnapshot> OpenSnapshotAsync(
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

        var restored = await _snapshotStore.TryRestoreAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (restored is not null)
        {
            return restored.Snapshot;
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
        return snapshot;
    }

}
