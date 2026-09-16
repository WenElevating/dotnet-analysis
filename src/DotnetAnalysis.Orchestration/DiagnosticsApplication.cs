using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Orchestration.Models;

namespace DotnetAnalysis.Orchestration;

/// <summary>
/// 协调目标、活动分析会话和快照集合，并以 generation 隔离替换期间的旧结果。
/// </summary>
public sealed class DiagnosticsApplication : IDiagnosticsApplication
{
    private readonly IProcessDiagnostics _diagnostics;
    private readonly ITargetProcessLauncher _launcher;
    private readonly TargetProcessFinder _finder;
    private readonly TargetCapabilityProbe _capabilityProbe;
    private readonly IMemorySnapshotAnalysisService _analysisService;
    private readonly IEventBus _eventBus;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateGate = new();
    private IAnalysisSession? _activeSession;
    private SnapshotCollection _snapshots;
    private DiagnosticsApplicationState _state;
    private Task? _closeTask;
    private bool _disposed;

    /// <summary>创建诊断应用上下文。</summary>
    /// <param name="diagnostics">Application 层诊断门面。</param>
    /// <param name="launcher">宿主无关的目标启动器。</param>
    /// <param name="finder">目标查找器。</param>
    /// <param name="capabilityProbe">目标能力探测器。</param>
    /// <param name="analysisService">快照分析服务。</param>
    /// <param name="eventBus">Application 层事件总线。</param>
    public DiagnosticsApplication(IProcessDiagnostics diagnostics, ITargetProcessLauncher launcher, TargetProcessFinder finder, TargetCapabilityProbe capabilityProbe, IMemorySnapshotAnalysisService analysisService, IEventBus eventBus)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _finder = finder ?? throw new ArgumentNullException(nameof(finder));
        _capabilityProbe = capabilityProbe ?? throw new ArgumentNullException(nameof(capabilityProbe));
        _analysisService = analysisService ?? throw new ArgumentNullException(nameof(analysisService));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        var generation = Guid.NewGuid();
        _state = new DiagnosticsApplicationState(generation, DiagnosticsApplicationPhase.Start);
        _snapshots = new SnapshotCollection(_analysisService);
    }

    /// <inheritdoc />
    public Guid Generation { get { lock (_stateGate) return _state.Generation; } }

    /// <inheritdoc />
    public DiagnosticsApplicationState State { get { lock (_stateGate) return _state; } }

    /// <inheritdoc />
    public IAnalysisSession? ActiveSession
    {
        get
        {
            lock (_stateGate)
            {
                return _activeSession is { State: ProcessDiagnosticsSessionState.Ended or ProcessDiagnosticsSessionState.Failed }
                    ? null
                    : _activeSession;
            }
        }
    }

    /// <inheritdoc />
    public ISnapshotCollection Snapshots { get { lock (_stateGate) return _snapshots; } }

    /// <inheritdoc />
    public Task<IReadOnlyList<TargetProcess>> FindTargetsAsync(ProcessFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        Guid generation;
        lock (_stateGate)
        {
            EnsureOpen();
            generation = _state.Generation;
        }
        return FindTargetsCoreAsync(filter, generation, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IAnalysisSession> AttachAsync(TargetProcess target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            var (generation, _) = await ReplaceContextAsync(cancellationToken).ConfigureAwait(false);
            using var operation = new Operations.OperationScope(generation, cancellationToken: cancellationToken);
            operation.Operation.TryStart();
            SetOperation(generation, operation, DiagnosticOperationStage.FindingTarget, DiagnosticOperationStatus.Running);
            try
            {
                var targetContext = await _capabilityProbe.ProbeAsync(target, cancellationToken).ConfigureAwait(false);
                var session = await AnalysisSession.AttachAsync(_diagnostics, targetContext, cancellationToken).ConfigureAwait(false);
                var stale = false;
                lock (_stateGate)
                {
                    stale = _state.Generation != generation || _disposed;
                    if (!stale)
                    {
                        _activeSession = session;
                        _state = new DiagnosticsApplicationState(generation, DiagnosticsApplicationPhase.TargetSelection, session.State, targetContext, session.Id, quality: session.Quality);
                    }
                }

                if (stale)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                    throw new OperationCanceledException("目标上下文已被替换。", cancellationToken);
                }

                operation.Operation.TryComplete(session.Id);
                SetOperation(generation, operation, DiagnosticOperationStage.Querying, DiagnosticOperationStatus.Succeeded, session.Id);
                await PublishStateAsync(new ProcessDiagnosticsSessionStateChanged(session.Id, session.State, DateTimeOffset.UtcNow, nameof(DiagnosticsApplication)), generation, CancellationToken.None).ConfigureAwait(false);
                return session;
            }
            catch (OperationCanceledException)
            {
                operation.Operation.TryCancel();
                SetFailed(generation, DiagnosticsErrorCode.CaptureCancelled, "附着操作已取消。", operation);
                throw;
            }
            catch (Exception ex)
            {
                var errorCode = ex is DiagnosticsException diagnosticsException ? diagnosticsException.ErrorCode : DiagnosticsErrorCode.TargetChanged;
                var failure = new DiagnosticFailure(errorCode, DiagnosticOperationStage.FindingTarget, false, ex.Message);
                operation.Operation.TryFail(failure);
                SetFailed(generation, errorCode, ex.Message, operation);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IAnalysisSession> LaunchAndAttachAsync(LaunchTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var generation = Guid.NewGuid();
        using var operation = new Operations.OperationScope(
            generation,
            options: new Operations.OperationOptions(target.StartupTimeout),
            cancellationToken: cancellationToken);
        operation.Operation.TryStart();
        await _lifecycleGate.WaitAsync(operation.CancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            await ReplaceContextAsync(operation.CancellationToken, generation).ConfigureAwait(false);
            SetOperation(generation, operation, DiagnosticOperationStage.FindingTarget, DiagnosticOperationStatus.Running);
            var request = new TargetProcessLaunchRequest(target.ExecutablePath, target.Arguments, target.WorkingDirectory, target.Strategy is LaunchTargetStrategy.TerminateOnFailure ? TargetProcessLaunchFailureStrategy.TerminateProcess : TargetProcessLaunchFailureStrategy.KeepProcess, target.StartupTimeout);
            var result = await _launcher.LaunchAsync(request, operation.CancellationToken).ConfigureAwait(false);
            var targetContext = await ProbeUntilReadyAsync(result.Target, operation.CancellationToken).ConfigureAwait(false);
            var session = await AnalysisSession.AttachAsync(_diagnostics, targetContext, operation.CancellationToken).ConfigureAwait(false);
            var stale = false;
            lock (_stateGate)
            {
                stale = _state.Generation != generation || _disposed;
                if (!stale)
                {
                    _activeSession = session;
                    _state = new DiagnosticsApplicationState(generation, DiagnosticsApplicationPhase.TargetSelection, session.State, targetContext, session.Id, quality: session.Quality);
                }
            }
            if (stale)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw new OperationCanceledException("启动上下文已被替换。", operation.CancellationToken);
            }
            operation.Operation.TryComplete(session.Id);
            SetOperation(generation, operation, DiagnosticOperationStage.Querying, DiagnosticOperationStatus.Succeeded, session.Id);
            await PublishStateAsync(new ProcessDiagnosticsSessionStateChanged(session.Id, session.State, DateTimeOffset.UtcNow, nameof(DiagnosticsApplication)), generation, CancellationToken.None).ConfigureAwait(false);
            return session;
        }
        catch (OperationCanceledException)
        {
            operation.Operation.TryCancel();
            SetFailed(generation, DiagnosticsErrorCode.CaptureCancelled, "启动并附着操作已取消或超时。", operation);
            throw;
        }
        catch (Exception exception)
        {
            var errorCode = exception is DiagnosticsException diagnosticsException ? diagnosticsException.ErrorCode : DiagnosticsErrorCode.TargetChanged;
            var failure = new DiagnosticFailure(errorCode, DiagnosticOperationStage.FindingTarget, false, exception.Message);
            operation.Operation.TryFail(failure);
            SetFailed(generation, errorCode, exception.Message, operation);
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ISnapshotAnalysis> OpenSnapshotAsync(string filePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            var (generation, _) = await ReplaceContextAsync(cancellationToken).ConfigureAwait(false);
            using var operation = new Operations.OperationScope(generation, cancellationToken: cancellationToken);
            operation.Operation.TryStart();
            SetOperation(generation, operation, DiagnosticOperationStage.Querying, DiagnosticOperationStatus.Running);
            try
            {
                var snapshot = await _diagnostics.OpenSnapshotAsync(filePath, cancellationToken).ConfigureAwait(false);
                if (snapshot.State is MemorySnapshotState.Analyzing)
                {
                    snapshot = (await _analysisService.AnalyzeAsync(snapshot, cancellationToken).ConfigureAwait(false)).Snapshot;
                }
                lock (_stateGate)
                {
                    if (_state.Generation != generation || _disposed) throw new OperationCanceledException("快照上下文已被替换。", cancellationToken);
                    _snapshots.AddImported(snapshot);
                    _state = new DiagnosticsApplicationState(generation, DiagnosticsApplicationPhase.SnapshotSelection, snapshot: snapshot);
                    var analysis = _snapshots.GetAnalysis(snapshot.Id);
                    operation.Operation.TryComplete(snapshot.Id);
                    SetOperation(generation, operation, DiagnosticOperationStage.Querying, DiagnosticOperationStatus.Succeeded, snapshotId: snapshot.Id);
                    return analysis;
                }
            }
            catch (OperationCanceledException)
            {
                operation.Operation.TryCancel();
                SetFailed(generation, DiagnosticsErrorCode.CaptureCancelled, "打开快照操作已取消。", operation);
                throw;
            }
            catch (Exception ex)
            {
                var errorCode = ex is DiagnosticsException diagnosticsException ? diagnosticsException.ErrorCode : DiagnosticsErrorCode.SnapshotFormatNotSupported;
                var failure = new DiagnosticFailure(errorCode, DiagnosticOperationStage.Querying, false, ex.Message);
                operation.Operation.TryFail(failure);
                SetFailed(generation, errorCode, ex.Message, operation);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        Task closeTask;
        lock (_stateGate)
        {
            if (_closeTask is null)
            {
                _closeTask = CloseCoreAsync();
            }
            closeTask = _closeTask;
        }

        try
        {
            await closeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_stateGate)
            {
                if (!_disposed
                    && ReferenceEquals(_closeTask, closeTask)
                    && (closeTask.IsFaulted || closeTask.IsCanceled))
                {
                    _closeTask = null;
                }
            }

            throw;
        }
    }

    private async Task CloseCoreAsync()
    {
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            IAnalysisSession? session;
            SnapshotCollection snapshots;
            Guid oldGeneration;
            lock (_stateGate)
            {
                if (_disposed) return;
                oldGeneration = _state.Generation;
                session = _activeSession;
                snapshots = _snapshots;
            }

            Exception? cleanupFailure = null;
            if (session is not null)
            {
                try { await session.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception) { cleanupFailure = exception; }
                try { await session.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { cleanupFailure ??= exception; }
                try
                {
                    await _eventBus.PublishAsync(new ProcessDiagnosticsSessionEnded(session.Id, DateTimeOffset.UtcNow, nameof(DiagnosticsApplication)) { Generation = oldGeneration }, CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }
            try { await snapshots.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { cleanupFailure ??= exception; }

            if (cleanupFailure is not null)
            {
                lock (_stateGate)
                {
                    if (session is not null
                        && session.State is ProcessDiagnosticsSessionState.Ended or ProcessDiagnosticsSessionState.Failed)
                    {
                        _activeSession = null;
                    }

                    if (!_disposed) _state = new DiagnosticsApplicationState(_state.Generation, DiagnosticsApplicationPhase.Failed);
                }
                throw cleanupFailure;
            }

            lock (_stateGate)
            {
                _activeSession = null;
                _disposed = true;
                _state = new DiagnosticsApplicationState(Guid.NewGuid(), DiagnosticsApplicationPhase.Closed);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await CloseAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<TargetProcess>> FindTargetsCoreAsync(ProcessFilter filter, Guid generation, CancellationToken cancellationToken)
    {
        var result = await _finder.FindAsync(filter, cancellationToken).ConfigureAwait(false);
        lock (_stateGate)
        {
            if (!_disposed && _state.Generation == generation) _state = new DiagnosticsApplicationState(generation, DiagnosticsApplicationPhase.TargetSelection);
        }
        return result;
    }

    private async Task<(Guid Generation, Guid PreviousGeneration)> ReplaceContextAsync(CancellationToken cancellationToken, Guid? requestedGeneration = null)
    {
        IAnalysisSession? previous;
        SnapshotCollection oldSnapshots;
        Guid generation;
        Guid previousGeneration;
        lock (_stateGate)
        {
            EnsureOpen();
            previous = _activeSession;
            oldSnapshots = _snapshots;
            previousGeneration = _state.Generation;
            generation = requestedGeneration ?? Guid.NewGuid();
            _state = new DiagnosticsApplicationState(generation, DiagnosticsApplicationPhase.TargetSelection);
        }

        Exception? cleanupFailure = null;
        try
        {
            if (previous is not null)
            {
                try { await previous.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception) { cleanupFailure = exception; }
                try { await previous.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { cleanupFailure ??= exception; }
                try
                {
                    await _eventBus.PublishAsync(new ProcessDiagnosticsSessionEnded(previous.Id, DateTimeOffset.UtcNow, nameof(DiagnosticsApplication)) { Generation = previousGeneration }, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
        finally
        {
            try { await oldSnapshots.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { cleanupFailure ??= exception; }
        }

        if (cleanupFailure is not null)
        {
            SetFailed(generation, DiagnosticsErrorCode.TargetExited, $"替换旧诊断上下文时资源收尾失败。{cleanupFailure.Message}");
            throw cleanupFailure;
        }

        lock (_stateGate)
        {
            EnsureOpen();
            _activeSession = null;
            _snapshots = new SnapshotCollection(_analysisService);
            _state = new DiagnosticsApplicationState(generation, DiagnosticsApplicationPhase.TargetSelection);
        }
        return (generation, previousGeneration);
    }

    private async ValueTask PublishStateAsync(ProcessDiagnosticsSessionStateChanged applicationEvent, Guid generation, CancellationToken cancellationToken)
    {
        await _eventBus.PublishAsync(applicationEvent with { Generation = generation }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TargetContext> ProbeUntilReadyAsync(TargetProcess target, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                var context = await _capabilityProbe.ProbeAsync(target, cancellationToken).ConfigureAwait(false);
                if (context.Capabilities.StandardSnapshot.IsAvailable)
                {
                    return context;
                }

                if (context.Capabilities.StandardSnapshot.ErrorCode is not DiagnosticsErrorCode.RuntimeNotSupported)
                {
                    return context;
                }
            }
            catch (DiagnosticsException exception) when (exception.ErrorCode == DiagnosticsErrorCode.RuntimeNotSupported)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void SetOperation(Guid generation, Operations.OperationScope scope, DiagnosticOperationStage stage, DiagnosticOperationStatus status, ProcessDiagnosticsSessionId? sessionId = null, MemorySnapshotId? snapshotId = null, DiagnosticFailure? failure = null)
    {
        lock (_stateGate)
        {
            if (_disposed || _state.Generation != generation) return;
            var operation = new DiagnosticOperationState(scope.Operation.OperationId, generation, stage, status, sessionId, snapshotId, scope.DeadlineUtc, isCancellable: status is DiagnosticOperationStatus.Pending or DiagnosticOperationStatus.Running, failure: failure);
            _state = new DiagnosticsApplicationState(generation, _state.Phase, _state.SessionState, _state.Target, _state.SessionId, _state.Snapshot, operation, _state.Quality, _state.Failure);
        }
    }

    private void SetFailed(Guid generation, DiagnosticsErrorCode errorCode, string reason, Operations.OperationScope? scope = null)
    {
        lock (_stateGate)
        {
            if (_disposed || _state.Generation != generation) return;
            var failure = new DiagnosticFailure(errorCode, DiagnosticOperationStage.FindingTarget, false, reason);
            var status = scope?.Operation.Status switch
            {
                DiagnosticOperationStatus.Canceled => DiagnosticOperationStatus.Canceled,
                DiagnosticOperationStatus.TimedOut => DiagnosticOperationStatus.TimedOut,
                _ => DiagnosticOperationStatus.Failed
            };
            var operation = scope is null
                ? _state.Operation
                : new DiagnosticOperationState(scope.Operation.OperationId, generation, DiagnosticOperationStage.FindingTarget, status, failure: failure);
            _state = new DiagnosticsApplicationState(generation, DiagnosticsApplicationPhase.Failed, operation: operation, failure: failure);
        }
    }

    private void EnsureOpen() => ObjectDisposedException.ThrowIf(_disposed, this);
}
