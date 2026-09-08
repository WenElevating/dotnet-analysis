using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;
using DotnetAnalysis.Diagnostics.Windows.Capture;
using Microsoft.Extensions.Logging;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 抽象会话持有的分配采样资源，以便进程诊断会话能按统一生命周期启动、中断和释放它。
/// </summary>
internal interface IAllocationSamplingSessionResource : IAsyncDisposable
{
    AllocationSamplingSession Collector { get; }

    Task StartAsync(TargetProcess target, CancellationToken cancellationToken);

    void MarkInterrupted(DateTimeOffset observedAtUtc);
}

/// <summary>
/// 将具体分配采样收集器适配为进程诊断会话拥有的资源契约。
/// </summary>
internal sealed class AllocationSamplingSessionResource : IAllocationSamplingSessionResource
{
    public AllocationSamplingSessionResource(AllocationSamplingSession collector)
    {
        Collector = collector ?? throw new ArgumentNullException(nameof(collector));
    }

    public AllocationSamplingSession Collector { get; }

    /// <summary>
    /// 启动收集器对目标进程的分配采样。
    /// </summary>
    public Task StartAsync(TargetProcess target, CancellationToken cancellationToken) =>
        Collector.StartAsync(target, cancellationToken);

    /// <summary>
    /// 记录目标或传输中断时间，供后续分配概要反映采样不完整状态。
    /// </summary>
    public void MarkInterrupted(DateTimeOffset observedAtUtc) =>
        Collector.MarkInterrupted(observedAtUtc);

    /// <summary>
    /// 释放底层收集器及其 EventPipe 资源。
    /// </summary>
    public ValueTask DisposeAsync() => Collector.DisposeAsync();
}

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
    private readonly IAllocationSamplingSessionResource _allocationSampling;
    private readonly IExecutionSamplingSession _executionSampling;
    private readonly IMemorySnapshotCapture _capture;
    private readonly IEventBus _eventBus;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProcessDiagnosticsSession> _logger;
    private readonly ProcessIdentityValidator _identityValidator;
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
    /// <param name="executionSampling">提供附着期执行采样查询的会话级资源。</param>
    /// <param name="capture">执行底层快照捕获的内部实现。</param>
    internal ProcessDiagnosticsSession(
        TargetProcess process,
        ProcessMemorySampler sampler,
        AllocationSamplingSession allocationCollector,
        IExecutionSamplingSession executionSampling,
        IMemorySnapshotCapture capture,
        IEventBus eventBus,
        TimeProvider timeProvider,
        ILogger<ProcessDiagnosticsSession> logger,
        ProcessIdentityValidator identityValidator)
        : this(
            process,
            sampler,
            new AllocationSamplingSessionResource(allocationCollector),
            executionSampling,
            capture,
            eventBus,
            timeProvider,
            logger,
            identityValidator)
    {
    }

    internal ProcessDiagnosticsSession(
        TargetProcess process,
        ProcessMemorySampler sampler,
        IAllocationSamplingSessionResource allocationSampling,
        IExecutionSamplingSession executionSampling,
        IMemorySnapshotCapture capture,
        IEventBus eventBus,
        TimeProvider timeProvider,
        ILogger<ProcessDiagnosticsSession> logger,
        ProcessIdentityValidator identityValidator)
    {
        Process = process ?? throw new ArgumentNullException(nameof(process));
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _allocationSampling = allocationSampling ?? throw new ArgumentNullException(nameof(allocationSampling));
        _executionSampling = executionSampling ?? throw new ArgumentNullException(nameof(executionSampling));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _identityValidator = identityValidator ?? throw new ArgumentNullException(nameof(identityValidator));
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
                await CompleteEndAfterTargetLossAsync().ConfigureAwait(false);
            }

            yield return sample;
        }
    }

    /// <summary>
    /// 查询当前附着会话已采集时间区间内的托管执行分析结果。
    /// </summary>
    /// <param name="timeRange">需要查询的 UTC 执行采样时间区间。</param>
    /// <param name="cancellationToken">取消本次查询或随会话结束停止查询的标记。</param>
    /// <returns>指定时间区间内的执行采样分析结果。</returns>
    /// <exception cref="DiagnosticsException">执行采样不可用、区间不可查询或读取失败时引发。</exception>
    public async Task<ExecutionProfile> GetExecutionProfileAsync(
        ExecutionTimeRange timeRange,
        CancellationToken cancellationToken) => await GetExecutionProfileAsync(
            timeRange,
            ExecutionProfileQueryMode.Incremental,
            cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<ExecutionProfile> GetExecutionProfileAsync(
        ExecutionTimeRange timeRange,
        ExecutionProfileQueryMode queryMode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeRange);
        cancellationToken.ThrowIfCancellationRequested();

        CancellationTokenSource queryCancellation;
        lock (_syncRoot)
        {
            if (State is not ProcessDiagnosticsSessionState.Monitoring)
            {
                throw CreateExecutionProfileRangeUnavailableException();
            }

            queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _endCancellation.Token);
        }

        using (queryCancellation)
        {
            await EnsureTargetIdentityCurrentAsync(queryCancellation.Token).ConfigureAwait(false);
            ExecutionProfile profile;
            try
            {
                profile = await _executionSampling
                    .GetExecutionProfileAsync(timeRange, queryMode, queryCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (DiagnosticsException exception)
            {
                if (IsTargetIdentityFailure(exception))
                {
                    await CompleteEndAfterTargetLossAsync().ConfigureAwait(false);
                    throw CreateExecutionProfileRangeUnavailableException();
                }

                await EnsureTargetIdentityCurrentAsync(queryCancellation.Token).ConfigureAwait(false);
                throw;
            }

            await EnsureTargetIdentityCurrentAsync(queryCancellation.Token).ConfigureAwait(false);
            return profile;
        }
    }

    /// <summary>
    /// 结束会话并释放由会话拥有的运行期资源。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await EndAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _endCancellation.Dispose();
        }
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
                .CaptureAsync(Process, _allocationSampling.Collector, captureCancellation.Token)
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
        ExceptionDispatchInfo? cleanupFailure = null;

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
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            cleanupFailure = ExceptionDispatchInfo.Capture(exception);
        }

        try
        {
            activeCaptureCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            cleanupFailure ??= ExceptionDispatchInfo.Capture(exception);
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

        try
        {
            await _executionSampling.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure ??= ExceptionDispatchInfo.Capture(exception);
        }

        try
        {
            await DisposeAllocationCollectorAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure ??= ExceptionDispatchInfo.Capture(exception);
        }

        try
        {
            await _sampler.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure ??= ExceptionDispatchInfo.Capture(exception);
        }

        try
        {
            lock (_syncRoot)
            {
                if (State is ProcessDiagnosticsSessionState.Ending)
                {
                    MoveTo(ProcessDiagnosticsSessionState.Ended);
                }
            }
        }
        catch (Exception exception)
        {
            cleanupFailure ??= ExceptionDispatchInfo.Capture(exception);
        }

        await PublishEventAsync(new ProcessDiagnosticsSessionEnded(
            Id,
            _timeProvider.GetUtcNow(),
            nameof(ProcessDiagnosticsSession))).ConfigureAwait(false);

        cleanupFailure?.Throw();
    }

    /// <summary>
    /// 释放为会话捕获操作提供分配概要的收集器。
    /// </summary>
    private async ValueTask DisposeAllocationCollectorAsync()
    {
        await _allocationSampling.DisposeAsync().ConfigureAwait(false);
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

        if (exception.ErrorCode is DiagnosticsErrorCode.TargetExited)
        {
            _ = CompleteEndAfterTargetLossAsync();
        }
    }

    /// <summary>
    /// 在涉及目标进程的操作前验证 PID 与启动时间仍匹配，并将目标丢失转换为会话结束。
    /// </summary>
    private async Task EnsureTargetIdentityCurrentAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _identityValidator.ValidateAsync(Process, cancellationToken).ConfigureAwait(false);
        }
        catch (DiagnosticsException exception) when (IsTargetIdentityFailure(exception))
        {
            await CompleteEndAfterTargetLossAsync().ConfigureAwait(false);
            throw CreateExecutionProfileRangeUnavailableException();
        }
    }

    /// <summary>
    /// 判断稳定诊断错误是否表示进程退出或 PID 被复用，二者都要求终止当前会话。
    /// </summary>
    private static bool IsTargetIdentityFailure(DiagnosticsException exception) =>
        exception.ErrorCode is DiagnosticsErrorCode.TargetExited or DiagnosticsErrorCode.TargetChanged;

    /// <summary>
    /// 在目标已丢失时尽力完成幂等结束；清理异常不覆盖原始身份验证失败。
    /// </summary>
    private async Task CompleteEndAfterTargetLossAsync() =>
        await EndAsync(CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    /// <summary>
    /// 为在目标丢失期间无法再读取的执行采样范围创建稳定错误。
    /// </summary>
    private static DiagnosticsException CreateExecutionProfileRangeUnavailableException() =>
        new(
            DiagnosticsErrorCode.ExecutionProfileRangeUnavailable,
            "The requested execution profile range is not available after the session has ended.");

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
