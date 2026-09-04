using System.Runtime.CompilerServices;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;
using DotnetAnalysis.Diagnostics.Windows.Capture;
using Microsoft.Extensions.Logging;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 统一拥有 Windows 进程诊断的状态、采样、快照捕获、资源和生命周期事件。
/// </summary>
public sealed class ProcessDiagnosticsSession : IProcessDiagnosticsSession
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
    private readonly ProcessMemorySampler _sampler;
    private readonly AllocationSamplingSession _allocationCollector;
    private readonly IMemorySnapshotCapture _capture;
    private readonly IEventBus _eventBus;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProcessDiagnosticsSession> _logger;
    private readonly CancellationTokenSource _endCancellation = new();
    private Task<MemorySnapshot>? _captureTask;
    private CancellationTokenSource? _captureCancellation;
    private Task? _endTask;

    /// <summary>
    /// 创建绑定目标进程、采样器和运行期资源的唯一诊断会话。
    /// </summary>
    /// <param name="process">会话绑定的目标进程身份。</param>
    /// <param name="sampler">提供进程内存使用样本的采样器。</param>
    /// <param name="eventBus">发布共享诊断生命周期事件的事件总线。</param>
    /// <param name="timeProvider">事件时间来源。</param>
    /// <param name="logger">记录失败和事件投递问题的日志记录器。</param>
    /// <param name="allocationCollector">为捕获封存分配概要的会话级收集器。</param>
    /// <param name="capture">执行底层快照捕获的内部实现。</param>
    internal ProcessDiagnosticsSession(
        TargetProcess process,
        ProcessMemorySampler sampler,
        AllocationSamplingSession allocationCollector,
        IMemorySnapshotCapture capture,
        IEventBus eventBus,
        TimeProvider timeProvider,
        ILogger<ProcessDiagnosticsSession> logger)
    {
        Process = process ?? throw new ArgumentNullException(nameof(process));
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _allocationCollector = allocationCollector ?? throw new ArgumentNullException(nameof(allocationCollector));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        State = ProcessDiagnosticsSessionState.Monitoring;
        _ = PublishEventAsync(new ProcessDiagnosticsSessionStateChanged(
            Id,
            State,
            _timeProvider.GetUtcNow(),
            nameof(ProcessDiagnosticsSession))).AsTask();
    }

    /// <summary>
    /// 当前诊断会话的稳定标识。
    /// </summary>
    public ProcessDiagnosticsSessionId Id { get; } = ProcessDiagnosticsSessionId.New();

    /// <summary>
    /// 会话绑定的目标进程。
    /// </summary>
    public TargetProcess Process { get; }

    /// <summary>
    /// 当前会话生命周期状态。
    /// </summary>
    public ProcessDiagnosticsSessionState State { get; private set; } = ProcessDiagnosticsSessionState.Attaching;

    /// <summary>
    /// 幂等结束会话，并取消进行中的捕获和采样。
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
                nameof(ProcessDiagnosticsSession))).AsTask();
            return _captureTask;
        }
    }

    /// <summary>
    /// 转发进程内存样本，并在目标已结束时收尾会话状态。
    /// </summary>
    public async IAsyncEnumerable<MemoryUsageSample> GetMemoryUsageAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _endCancellation.Token);
        await foreach (var sample in _sampler.GetSamplesAsync(linked.Token).ConfigureAwait(false))
        {
            if (sample.State is MemoryUsageSampleState.SessionEnded)
            {
                MoveToEnded();
            }

            yield return sample;
        }
    }

    /// <summary>
    /// 结束会话并释放由会话拥有的运行期资源。
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
            var snapshot = await _capture
                .CaptureAsync(Process, _allocationCollector, captureCancellation.Token)
                .ConfigureAwait(false);
            _ = PublishEventAsync(new MemorySnapshotCaptured(
                Id,
                snapshot.Id,
                _timeProvider.GetUtcNow(),
                nameof(ProcessDiagnosticsSession))).AsTask();
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
                nameof(ProcessDiagnosticsSession))).AsTask();
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
                nameof(ProcessDiagnosticsSession))).AsTask();
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
                nameof(ProcessDiagnosticsSession))).AsTask();
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
    /// 协调取消、等待活动捕获、释放运行期资源并完成结束状态迁移。
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

        await DisposeAllocationCollectorAsync().ConfigureAwait(false);
        await _sampler.DisposeAsync().ConfigureAwait(false);

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
            nameof(ProcessDiagnosticsSession))).AsTask();
    }

    /// <summary>
    /// 释放为会话捕获操作提供分配概要的收集器。
    /// </summary>
    private async ValueTask DisposeAllocationCollectorAsync()
    {
        await _allocationCollector.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 按稳定错误码记录失败并推进会话状态。
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
    /// 在采样器报告目标结束时完成会话状态迁移。
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
            nameof(ProcessDiagnosticsSession))).AsTask();
    }

    /// <summary>
    /// 尽力发布生命周期事件，发布失败只记录日志。
    /// </summary>
    private async ValueTask PublishEventAsync(IApplicationEvent applicationEvent)
    {
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
