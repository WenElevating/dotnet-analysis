using System.Runtime.CompilerServices;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;
using Microsoft.Extensions.Logging;

namespace DotnetAnalysis.Application.Sessions;

/// <summary>
/// 应用层会话包装器，负责状态、取消、事件和底层会话生命周期。
/// </summary>
public sealed class AttachedProcessSession : IProcessDiagnosticsSession
{
    private const string CaptureSnapshotStage = "CaptureSnapshot";

    private static readonly Action<ILogger, ProcessDiagnosticsSessionId, string, Exception?> s_stageFailed =
        LoggerMessage.Define<ProcessDiagnosticsSessionId, string>(
            LogLevel.Error,
            new EventId(1, "DiagnosticsSessionStageFailed"),
            "Diagnostics session {SessionId} failed during {Stage}.");

    private static readonly Action<ILogger, string, Exception?> s_eventPublishFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2, "DiagnosticsSessionEventPublishFailed"),
            "Could not publish diagnostics session event {EventType}.");

    private readonly object _syncRoot = new();
    private readonly IProcessDiagnosticsSession _diagnosticSession;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AttachedProcessSession> _logger;
    private readonly IEventBus? _eventBus;
    private readonly CancellationTokenSource _endCancellation = new();
    private Task<MemorySnapshot>? _captureTask;
    private CancellationTokenSource? _captureCancellation;
    private Task? _endTask;
    private bool _disposedInnerSession;

    /// <summary>
    /// 创建不发布事件的附着会话。
    /// </summary>
    /// <param name="id">会话标识。</param>
    /// <param name="diagnosticSession">底层诊断会话。</param>
    /// <param name="timeProvider">事件时间来源。</param>
    /// <param name="logger">记录阶段失败的日志记录器。</param>
    public AttachedProcessSession(
        ProcessDiagnosticsSessionId id,
        IProcessDiagnosticsSession diagnosticSession,
        TimeProvider timeProvider,
        ILogger<AttachedProcessSession> logger)
        : this(id, diagnosticSession, null, timeProvider, logger)
    {
    }

    /// <summary>
    /// 创建可选发布生命周期事件的附着会话。
    /// </summary>
    /// <param name="id">会话标识。</param>
    /// <param name="diagnosticSession">底层诊断会话。</param>
    /// <param name="eventBus">可选事件总线。</param>
    /// <param name="timeProvider">事件时间来源。</param>
    /// <param name="logger">记录阶段失败的日志记录器。</param>
    public AttachedProcessSession(
        ProcessDiagnosticsSessionId id,
        IProcessDiagnosticsSession diagnosticSession,
        IEventBus? eventBus,
        TimeProvider timeProvider,
        ILogger<AttachedProcessSession> logger)
    {
        Id = id;
        _diagnosticSession = diagnosticSession ?? throw new ArgumentNullException(nameof(diagnosticSession));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _eventBus = eventBus;
        State = ProcessDiagnosticsSessionState.Monitoring;
        _ = PublishEventAsync(new ProcessDiagnosticsSessionStateChanged(
            Id,
            State,
            _timeProvider.GetUtcNow(),
            nameof(AttachedProcessSession))).AsTask();
    }

    /// <summary>
    /// 应用层会话标识。
    /// </summary>
    public ProcessDiagnosticsSessionId Id { get; }

    /// <summary>
    /// 底层会话绑定的目标进程。
    /// </summary>
    public TargetProcess Process => _diagnosticSession.Process;

    /// <summary>
    /// 当前会话生命周期状态。
    /// </summary>
    public ProcessDiagnosticsSessionState State { get; private set; } = ProcessDiagnosticsSessionState.Attaching;

    /// <summary>
    /// 幂等结束会话，并取消进行中的捕获。
    /// </summary>
    public Task EndAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            if (_endTask is not null)
            {
                return _endTask;
            }

            _endTask = EndCoreAsync();
            return _endTask;
        }
    }

    /// <summary>
    /// 启动一次快照捕获；并发捕获或结束后的调用会被拒绝。
    /// </summary>
    public Task<MemorySnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(
                State is ProcessDiagnosticsSessionState.Ending or ProcessDiagnosticsSessionState.Ended,
                this);

            if (State is ProcessDiagnosticsSessionState.Failed)
            {
                throw new InvalidOperationException($"Session {Id} cannot capture from state {State}.");
            }

            if (_captureTask is not null)
            {
                throw new InvalidOperationException($"Session {Id} already has an active capture.");
            }

            _captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _endCancellation.Token);
            _captureTask = CaptureCoreAsync(_captureCancellation);
            _ = PublishEventAsync(new MemorySnapshotCaptureStarted(
                Id,
                _timeProvider.GetUtcNow(),
                nameof(AttachedProcessSession))).AsTask();
            return _captureTask;
        }
    }

    /// <summary>
    /// 转发底层内存样本，并同步会话结束状态。
    /// </summary>
    public async IAsyncEnumerable<MemoryUsageSample> GetMemoryUsageAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var sample in _diagnosticSession.GetMemoryUsageAsync(cancellationToken).ConfigureAwait(false))
        {
            if (sample.State is MemoryUsageSampleState.SessionEnded)
            {
                MoveToEnded();
            }

            yield return sample;
        }
    }

    /// <summary>
    /// 结束会话并释放底层诊断资源。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await EndAsync(CancellationToken.None).ConfigureAwait(false);
        _endCancellation.Dispose();
    }

    /// <summary>
    /// 执行底层捕获并发布成功、取消或失败事件。
    /// </summary>
    private async Task<MemorySnapshot> CaptureCoreAsync(CancellationTokenSource captureCancellation)
    {
        await Task.Yield();

        try
        {
            var snapshot = await _diagnosticSession.CaptureSnapshotAsync(captureCancellation.Token).ConfigureAwait(false);
            _ = PublishEventAsync(new MemorySnapshotCaptured(
                Id,
                snapshot.Id,
                _timeProvider.GetUtcNow(),
                nameof(AttachedProcessSession))).AsTask();
            return snapshot;
        }
        catch (OperationCanceledException exception) when (captureCancellation.IsCancellationRequested)
        {
            var diagnosticsException = new DiagnosticsException(
                DiagnosticsErrorCode.CaptureCancelled,
                "Snapshot capture was cancelled.",
                exception);
            _ = PublishEventAsync(new MemorySnapshotCaptureFailed(
                Id,
                diagnosticsException.ErrorCode,
                diagnosticsException.Message,
                _timeProvider.GetUtcNow(),
                nameof(AttachedProcessSession))).AsTask();
            throw diagnosticsException;
        }
        catch (DiagnosticsException exception)
        {
            HandleDiagnosticsException(exception);
            _ = PublishEventAsync(new MemorySnapshotCaptureFailed(
                Id,
                exception.ErrorCode,
                "Snapshot capture failed.",
                _timeProvider.GetUtcNow(),
                nameof(AttachedProcessSession))).AsTask();
            throw;
        }
        catch (Exception exception)
        {
            s_stageFailed(_logger, Id, CaptureSnapshotStage, exception);
            var diagnosticsException = new DiagnosticsException(
                DiagnosticsErrorCode.CaptureFailed,
                "Snapshot capture failed.",
                exception);
            _ = PublishEventAsync(new MemorySnapshotCaptureFailed(
                Id,
                diagnosticsException.ErrorCode,
                diagnosticsException.Message,
                _timeProvider.GetUtcNow(),
                nameof(AttachedProcessSession))).AsTask();
            throw diagnosticsException;
        }
        finally
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_captureCancellation, captureCancellation))
                {
                    _captureTask = null;
                    _captureCancellation = null;
                }
            }

            captureCancellation.Dispose();
        }
    }

    /// <summary>
    /// 协调取消、等待活动捕获并释放底层会话。
    /// </summary>
    private async Task EndCoreAsync()
    {
        Task<MemorySnapshot>? activeCapture;
        CancellationTokenSource? activeCaptureCancellation;

        lock (_syncRoot)
        {
            if (State is ProcessDiagnosticsSessionState.Monitoring)
            {
                MoveTo(ProcessDiagnosticsSessionState.Ending);
            }

            activeCapture = _captureTask;
            activeCaptureCancellation = _captureCancellation;
        }

        try
        {
            _endCancellation.Cancel();
            activeCaptureCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (activeCapture is not null)
        {
            try
            {
                await activeCapture.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        await DisposeInnerSessionOnceAsync().ConfigureAwait(false);

        lock (_syncRoot)
        {
            if (State is ProcessDiagnosticsSessionState.Ending)
            {
                MoveTo(ProcessDiagnosticsSessionState.Ended);
            }
        }

        _ = PublishEventAsync(new ProcessDiagnosticsSessionEnded(
            Id,
            _timeProvider.GetUtcNow(),
            nameof(AttachedProcessSession))).AsTask();
    }

    /// <summary>
    /// 确保底层诊断会话只被异步释放一次。
    /// </summary>
    private async ValueTask DisposeInnerSessionOnceAsync()
    {
        lock (_syncRoot)
        {
            if (_disposedInnerSession)
            {
                return;
            }

            _disposedInnerSession = true;
        }

        await _diagnosticSession.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 按稳定错误码记录失败并推进应用层会话状态。
    /// </summary>
    private void HandleDiagnosticsException(DiagnosticsException exception)
    {
        s_stageFailed(_logger, Id, CaptureSnapshotStage, exception);

        lock (_syncRoot)
        {
            switch (exception.ErrorCode)
            {
                case DiagnosticsErrorCode.TargetExited:
                    if (State is ProcessDiagnosticsSessionState.Monitoring)
                    {
                        MoveTo(ProcessDiagnosticsSessionState.Ending);
                        MoveTo(ProcessDiagnosticsSessionState.Ended);
                    }

                    break;
                case DiagnosticsErrorCode.AccessDenied:
                case DiagnosticsErrorCode.TargetChanged:
                case DiagnosticsErrorCode.RuntimeNotSupported:
                    if (State is ProcessDiagnosticsSessionState.Monitoring)
                    {
                        MoveTo(ProcessDiagnosticsSessionState.Failed);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// 在底层报告结束时完成 Ending 到 Ended 的状态转移。
    /// </summary>
    private void MoveToEnded()
    {
        lock (_syncRoot)
        {
            if (State is ProcessDiagnosticsSessionState.Monitoring)
            {
                MoveTo(ProcessDiagnosticsSessionState.Ending);
                MoveTo(ProcessDiagnosticsSessionState.Ended);
            }
        }
    }

    /// <summary>
    /// 校验并执行一次会话状态转移，同时发布状态事件。
    /// </summary>
    private void MoveTo(ProcessDiagnosticsSessionState nextState)
    {
        if (!ProcessDiagnosticsSessionTransitionRules.CanMove(State, nextState))
        {
            throw new InvalidOperationException($"Session {Id} cannot move from {State} to {nextState}.");
        }

        State = nextState;
        _ = PublishEventAsync(new ProcessDiagnosticsSessionStateChanged(
            Id,
            nextState,
            _timeProvider.GetUtcNow(),
            nameof(AttachedProcessSession))).AsTask();
    }

    /// <summary>
    /// 尽力发布生命周期事件，发布失败只记录日志。
    /// </summary>
    private async ValueTask PublishEventAsync(IApplicationEvent applicationEvent)
    {
        if (_eventBus is null)
        {
            return;
        }

        try
        {
            await _eventBus.PublishAsync(applicationEvent, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            s_eventPublishFailed(_logger, applicationEvent.GetType().Name, exception);
        }
    }
}
