using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Application.Events;

namespace DotnetAnalysis.Orchestration.Operations;

/// <summary>
/// 持有单个诊断操作的链接取消、截止时间和代次隔离资源。
/// </summary>
public sealed class OperationScope : IDisposable, IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private readonly CancellationTokenRegistration _externalCancellationRegistration;
    private readonly ITimer? _deadlineTimer;
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>创建操作作用域。</summary>
    /// <param name="generation">应用上下文代次。</param>
    /// <param name="sessionId">关联的诊断会话；全局操作可为空。</param>
    /// <param name="options">操作时钟和超时配置。</param>
    /// <param name="cancellationToken">宿主提供的外部取消令牌。</param>
    public OperationScope(
        Guid generation,
        ProcessDiagnosticsSessionId? sessionId = null,
        OperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new OperationOptions();
        var now = options.TimeProvider.GetUtcNow();
        DateTimeOffset? deadline = options.Timeout is { } timeout ? now + timeout : null;
        Operation = new DiagnosticOperation(generation, sessionId, deadline);
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _externalCancellationRegistration = cancellationToken.Register(static state => ((OperationScope)state!).CancelFromCaller(), this);
        if (options.Timeout is { } deadlineTimeout)
        {
            _deadlineTimer = options.TimeProvider.CreateTimer(static state => ((OperationScope)state!).TimeoutFromClock(), this, deadlineTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>创建操作作用域并使用显式超时时长。</summary>
    /// <param name="generation">应用上下文代次。</param>
    /// <param name="timeout">从创建时刻开始计算的超时时长。</param>
    /// <param name="timeProvider">提供当前时间和定时器的时钟。</param>
    /// <param name="cancellationToken">宿主提供的外部取消令牌。</param>
    public OperationScope(Guid generation, TimeSpan timeout, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
        : this(generation, null, new OperationOptions(timeout, timeProvider), cancellationToken)
    {
    }

    /// <summary>当前操作的稳定状态对象。</summary>
    public DiagnosticOperation Operation { get; }

    /// <summary>操作使用的链接取消令牌。</summary>
    public CancellationToken CancellationToken => _cancellation.Token;

    /// <summary>操作截止时间。</summary>
    public DateTimeOffset? DeadlineUtc => Operation.DeadlineUtc;

    /// <summary>判断外部结果是否仍属于当前操作作用域。</summary>
    /// <param name="generation">结果携带的应用代次。</param>
    /// <param name="sessionId">结果携带的诊断会话。</param>
    /// <param name="operationId">结果携带的操作身份。</param>
    /// <returns>三项身份全部匹配且作用域尚未释放时返回 <see langword="true"/>。</returns>
    public bool AcceptsResult(Guid generation, ProcessDiagnosticsSessionId? sessionId, Guid operationId)
    {
        lock (_gate)
        {
            return !_disposed
                && !CancellationToken.IsCancellationRequested
                && Operation.Generation == generation
                && Operation.SessionId == sessionId
                && Operation.OperationId == operationId;
        }
    }

    /// <summary>判断应用事件是否仍属于当前操作作用域。</summary>
    /// <param name="applicationEvent">携带代次、会话和操作身份的事件。</param>
    /// <returns>事件属于当前未取消作用域时返回 <see langword="true"/>。</returns>
    public bool AcceptsResult(IOperationEvent applicationEvent)
    {
        ArgumentNullException.ThrowIfNull(applicationEvent);
        return AcceptsResult(applicationEvent.Generation, applicationEvent is DotnetAnalysis.Core.Events.IApplicationEvent applicationEventBase ? applicationEventBase.SessionId : null, applicationEvent.OperationId ?? Guid.Empty);
    }

    /// <summary>请求调用方取消当前操作。</summary>
    public bool TryCancel()
    {
        var changed = Operation.TryCancel();
        CancelToken();
        return changed;
    }

    /// <summary>释放作用域并取消、清理所有内部资源。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Operation.TryCancel();
        CancelToken();
        _externalCancellationRegistration.Dispose();
        _deadlineTimer?.Dispose();
        _cancellation.Dispose();
    }

    /// <summary>异步释放作用域；当前实现没有异步清理工作。</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void CancelFromCaller()
    {
        Operation.TryCancel();
        CancelToken();
    }

    private void TimeoutFromClock()
    {
        Operation.TryTimeout();
        CancelToken();
    }

    private void CancelToken()
    {
        try { _cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
