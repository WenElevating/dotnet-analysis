using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Orchestration.Models;
using DotnetAnalysis.Orchestration.Operations;

namespace DotnetAnalysis.Orchestration;

/// <summary>
/// 拥有底层诊断会话、样本流、捕获和执行查询生命周期的活动分析会话。
/// </summary>
public sealed class AnalysisSession : IAnalysisSession
{
    private readonly IProcessDiagnosticsSession _diagnosticsSession;
    private readonly CancellationTokenSource _lifetimeCancellation;
    private readonly SessionStateMachine _stateMachine = new();
    private readonly object _gate = new();
    private readonly List<Task> _operations = [];
    private readonly List<CancellationTokenSource> _operationCancellations = [];
    private readonly MemoryTimeline _timeline;
    private Task? _samplingTask;
    private Task? _stopTask;
    private DiagnosticFailure? _failure;
    private bool _disposed;

    private AnalysisSession(
        TargetContext target,
        IProcessDiagnosticsSession diagnosticsSession,
        CancellationTokenSource lifetimeCancellation,
        MemoryTimeline timeline)
    {
        Target = target;
        _diagnosticsSession = diagnosticsSession;
        _lifetimeCancellation = lifetimeCancellation;
        _timeline = timeline;
        _stateMachine.MoveTo(ProcessDiagnosticsSessionState.Monitoring);
    }

    /// <summary>附着目标并创建活动分析会话。</summary>
    /// <param name="diagnostics">Application 层诊断门面。</param>
    /// <param name="target">已完成能力描述的目标上下文。</param>
    /// <param name="cancellationToken">取消附着的令牌。</param>
    /// <param name="timelineCapacity">时间线最大样本数。</param>
    /// <returns>已拥有底层诊断会话的编排会话。</returns>
    public static async Task<AnalysisSession> AttachAsync(
        IProcessDiagnostics diagnostics,
        TargetContext target,
        CancellationToken cancellationToken,
        int timelineCapacity = 2048)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(target);
        var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var diagnosticsSession = await diagnostics
                .AttachAsync(target.Target, lifetimeCancellation.Token)
                .ConfigureAwait(false);
            var session = new AnalysisSession(
                target,
                diagnosticsSession,
                lifetimeCancellation,
                new MemoryTimeline(timelineCapacity));
            session._samplingTask = session.ConsumeSamplesAsync();
            return session;
        }
        catch
        {
            lifetimeCancellation.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 使用既有操作作用域附着目标，继承其取消和截止时间。
    /// </summary>
    /// <param name="diagnostics">Application 层诊断门面。</param>
    /// <param name="target">已完成能力描述的目标上下文。</param>
    /// <param name="operationScope">提供代次隔离和链接取消的操作作用域。</param>
    /// <param name="timelineCapacity">时间线最大样本数。</param>
    /// <returns>已拥有底层诊断会话的编排会话。</returns>
    public static Task<AnalysisSession> AttachAsync(
        IProcessDiagnostics diagnostics,
        TargetContext target,
        OperationScope operationScope,
        int timelineCapacity = 2048) =>
        AttachAsync(diagnostics, target, operationScope.CancellationToken, timelineCapacity);

    /// <inheritdoc />
    public TargetContext Target { get; }

    /// <inheritdoc />
    public ProcessDiagnosticsSessionId Id => _diagnosticsSession.Id;

    /// <inheritdoc />
    public ProcessDiagnosticsSessionState State => _stateMachine.State;

    /// <inheritdoc />
    public DiagnosticCapabilities Capabilities => Target.Capabilities;

    /// <inheritdoc />
    public MemoryTimeline Timeline => _timeline;

    /// <inheritdoc />
    public DiagnosticQualitySummary Quality => _timeline.Quality;

    /// <inheritdoc />
    public DiagnosticFailure? Failure
    {
        get { lock (_gate) return _failure; }
    }

    /// <inheritdoc />
    public Task<MemorySnapshot> CaptureAsync(
        MemorySnapshotCaptureMode captureMode,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(captureMode))
        {
            throw new ArgumentOutOfRangeException(nameof(captureMode), captureMode, "Unknown capture mode.");
        }

        var availability = captureMode is MemorySnapshotCaptureMode.RetentionAnalysis
            ? Capabilities.RetentionSnapshot
            : Capabilities.StandardSnapshot;
        if (!availability.IsAvailable)
        {
            throw new DiagnosticsException(
                availability.ErrorCode ?? (captureMode is MemorySnapshotCaptureMode.RetentionAnalysis
                    ? DiagnosticsErrorCode.ProfilerAttachUnavailable
                    : DiagnosticsErrorCode.CaptureFailed),
                availability.Reason ?? "请求的快照捕获能力不可用。");
        }

        return StartOperationAsync(
            token => _diagnosticsSession.CaptureSnapshotAsync(captureMode, token),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ExecutionProfile> GetExecutionProfileAsync(
        ExecutionTimeRange timeRange,
        ExecutionProfileQueryMode queryMode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeRange);
        if (!Capabilities.ExecutionSampling.IsAvailable)
        {
            throw new DiagnosticsException(
                Capabilities.ExecutionSampling.ErrorCode ?? DiagnosticsErrorCode.ExecutionProfilingUnavailable,
                Capabilities.ExecutionSampling.Reason ?? "执行采样能力不可用。");
        }

        if (!_timeline.Contains(timeRange))
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.ExecutionProfileRangeUnavailable,
                "执行采样查询区间不在当前会话已观察范围内。");
        }

        return StartOperationAsync(
            token => _diagnosticsSession.GetExecutionProfileAsync(timeRange, queryMode, token),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _stopTask ??= StopCoreAsync();
        }

        return _stopTask.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
        }

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _disposed = true;
            _lifetimeCancellation.Dispose();
        }
    }

    private async Task<T> StartOperationAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken callerCancellation)
    {
        CancellationTokenSource operationCancellation;
        Task<T> operationTask;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State is not ProcessDiagnosticsSessionState.Monitoring)
            {
                throw new DiagnosticsException(
                    DiagnosticsErrorCode.TargetExited,
                    "当前诊断会话已不再处于监控状态。");
            }

            operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                callerCancellation,
                _lifetimeCancellation.Token);
            _operationCancellations.Add(operationCancellation);
            operationTask = ExecuteOperationAsync(operation, operationCancellation.Token);
            _operations.Add(operationTask);
        }

        try
        {
            return await operationTask.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _operations.Remove(operationTask);
                _operationCancellations.Remove(operationCancellation);
            }
            operationCancellation.Dispose();
        }
    }

    private async Task<T> ExecuteOperationAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.CaptureCancelled, "当前诊断操作已取消。");
        }
        catch (DiagnosticsException exception)
        {
            SetFailure(exception);
            throw;
        }
    }

    private async Task ConsumeSamplesAsync()
    {
        try
        {
            await foreach (var sample in _diagnosticsSession
                .GetMemoryUsageAsync(_lifetimeCancellation.Token)
                .ConfigureAwait(false))
            {
                _timeline.Append(sample);
                if (sample.State is MemoryUsageSampleState.SessionEnded)
                {
                    SetFailure(new DiagnosticsException(
                        DiagnosticsErrorCode.TargetExited,
                        "目标进程已退出，活动诊断会话已结束。"));
                    _stateMachine.TryMoveTo(ProcessDiagnosticsSessionState.Failed);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (DiagnosticsException exception)
        {
            SetFailure(exception);
            _stateMachine.TryMoveTo(ProcessDiagnosticsSessionState.Failed);
        }
        catch (Exception exception)
        {
            var diagnosticsException = new DiagnosticsException(
                DiagnosticsErrorCode.CaptureFailed,
                "活动内存样本流失败。",
                exception);
            SetFailure(diagnosticsException);
            _stateMachine.TryMoveTo(ProcessDiagnosticsSessionState.Failed);
        }
    }

    private async Task StopCoreAsync()
    {
        lock (_gate)
        {
            if (State is ProcessDiagnosticsSessionState.Monitoring)
            {
                _stateMachine.MoveTo(ProcessDiagnosticsSessionState.Ending);
            }
            _lifetimeCancellation.Cancel();
            foreach (var cancellation in _operationCancellations)
            {
                cancellation.Cancel();
            }
        }

        Exception? cleanupFailure = null;
        try
        {
            await _diagnosticsSession.EndAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        var samplingTask = _samplingTask;
        if (samplingTask is not null)
        {
            try { await samplingTask.ConfigureAwait(false); }
            catch (Exception exception) { cleanupFailure ??= exception; }
        }

        Task[] operations;
        lock (_gate) operations = _operations.ToArray();
        if (operations.Length > 0)
        {
            try { await Task.WhenAll(operations).ConfigureAwait(false); }
            catch (Exception exception) { cleanupFailure ??= exception; }
        }

        if (cleanupFailure is not null)
        {
            var diagnosticsException = cleanupFailure as DiagnosticsException ?? new DiagnosticsException(
                DiagnosticsErrorCode.TargetExited,
                "活动诊断会话收尾失败。",
                cleanupFailure);
            SetFailure(diagnosticsException);
            _stateMachine.TryMoveTo(ProcessDiagnosticsSessionState.Failed);
            throw diagnosticsException;
        }

        _stateMachine.TryMoveTo(ProcessDiagnosticsSessionState.Ended);
    }

    private void SetFailure(DiagnosticsException exception)
    {
        lock (_gate)
        {
            _failure ??= new DiagnosticFailure(
                exception.ErrorCode,
                DiagnosticOperationStage.Querying,
                exception.ErrorCode is DiagnosticsErrorCode.CaptureCancelled,
                exception.Message);
        }
    }
}


