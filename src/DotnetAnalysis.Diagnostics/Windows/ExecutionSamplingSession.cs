using System.Runtime.ExceptionServices;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Extensions.Logging;

namespace DotnetAnalysis.Diagnostics.Windows;

internal interface IExecutionSamplingSession : IAsyncDisposable
{
    Task StartAsync(TargetProcess target, CancellationToken cancellationToken);

    Task<ExecutionProfile> GetExecutionProfileAsync(
        ExecutionTimeRange timeRange,
        CancellationToken cancellationToken);
}

/// <summary>
/// 拥有附着期执行采样、连续查询和会话私有资源的生命周期。
/// </summary>
internal sealed class ExecutionSamplingSession : IExecutionSamplingSession
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly object _stateLock = new();
    private readonly ExecutionCaptureStore _store;
    private readonly IEventPipeExecutionSampler _sampler;
    private readonly ExecutionSymbolResolver _symbolResolver;
    private readonly ExecutionProfileBuilder _profileBuilder;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _retryDelayAsync;

    private ExecutionSamplingSessionState _state;
    private DateTimeOffset _samplingStartedAtUtc;
    private DiagnosticsException? _unavailableFailure;
    private Task? _startTask;
    private Task? _disposeTask;
    private int _activeQueryCount;
    private TaskCompletionSource? _queriesDrained;

    /// <summary>
    /// 使用默认会话存储、EventPipe 读取器和本地符号解析器创建执行采样会话。
    /// </summary>
    internal ExecutionSamplingSession(
        string? storageRootDirectory = null,
        ILogger<ExecutionSymbolResolver>? symbolLogger = null)
        : this(CreateComponents(storageRootDirectory, symbolLogger))
    {
    }

    internal ExecutionSamplingSession(
        ExecutionCaptureStore store,
        IEventPipeExecutionSampler sampler,
        ExecutionSymbolResolver symbolResolver,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelayAsync = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _symbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
        _profileBuilder = new ExecutionProfileBuilder(
            _store,
            sourceLocationResolver: _symbolResolver.ResolveAsync);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retryDelayAsync = retryDelayAsync ?? Task.Delay;
    }

    private ExecutionSamplingSession(ExecutionSamplingComponents components)
        : this(components.Store, components.Sampler, components.SymbolResolver)
    {
    }

    /// <summary>
    /// 启动执行采样；瞬时传输失败只延迟一秒重试一次。
    /// </summary>
    public Task StartAsync(TargetProcess target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(
                _state is ExecutionSamplingSessionState.Disposing or ExecutionSamplingSessionState.Disposed,
                this);
            if (_startTask is not null)
            {
                return cancellationToken.CanBeCanceled
                    ? _startTask.WaitAsync(cancellationToken)
                    : _startTask;
            }

            _state = ExecutionSamplingSessionState.Starting;
            _startTask = StartCoreAsync(target, cancellationToken);
            return _startTask;
        }
    }

    /// <summary>
    /// 在当前已提交水位内固定读取边界并构建执行分析结果。
    /// </summary>
    public async Task<ExecutionProfile> GetExecutionProfileAsync(
        ExecutionTimeRange timeRange,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeRange);
        cancellationToken.ThrowIfCancellationRequested();

        ExecutionCaptureReadBoundary boundary;
        long lostEventCount;
        lock (_stateLock)
        {
            if (_state is ExecutionSamplingSessionState.Unavailable)
            {
                throw _unavailableFailure!;
            }

            if (_state is not ExecutionSamplingSessionState.Running)
            {
                throw CreateRangeUnavailableException();
            }

            var terminalFailure = _sampler.TerminalFailure;
            if (terminalFailure is not null)
            {
                throw terminalFailure;
            }

            boundary = _store.CaptureReadBoundary() with { StartedAtUtc = _samplingStartedAtUtc };
            if (timeRange.StartAtUtc < _samplingStartedAtUtc
                || timeRange.EndAtUtc > boundary.WrittenThroughUtc)
            {
                throw CreateRangeUnavailableException();
            }

            lostEventCount = _sampler.LostEventCount;
            _activeQueryCount++;
        }

        try
        {
            return await _profileBuilder.BuildAsync(
                timeRange,
                boundary,
                lostEventCount,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExitQuery();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _state = ExecutionSamplingSessionState.Disposing;
            _queriesDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_activeQueryCount == 0)
            {
                _queriesDrained.TrySetResult();
            }

            _disposeTask = DisposeCoreAsync(_startTask, _queriesDrained.Task);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task StartCoreAsync(TargetProcess target, CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var attemptStartedAtUtc = _timeProvider.GetUtcNow();
                try
                {
                    await _sampler.StartAsync(target, cancellationToken).ConfigureAwait(false);
                    lock (_stateLock)
                    {
                        if (_state is ExecutionSamplingSessionState.Starting)
                        {
                            _samplingStartedAtUtc = attemptStartedAtUtc;
                            _state = ExecutionSamplingSessionState.Running;
                        }
                    }

                    return;
                }
                catch (Exception exception) when (IsTransientStartFailure(exception) && attempt == 0)
                {
                    await _retryDelayAsync(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsMappedStartFailure(exception))
                {
                    lock (_stateLock)
                    {
                        if (_state is ExecutionSamplingSessionState.Starting)
                        {
                            _unavailableFailure = new DiagnosticsException(
                                DiagnosticsErrorCode.ExecutionProfilingUnavailable,
                                "The target runtime does not expose execution sampling.",
                                exception);
                            _state = ExecutionSamplingSessionState.Unavailable;
                        }
                    }

                    return;
                }
            }
        }
        finally
        {
            lock (_stateLock)
            {
                if (_state is ExecutionSamplingSessionState.Starting)
                {
                    _state = ExecutionSamplingSessionState.Created;
                    _startTask = null;
                }
            }
        }
    }

    private async Task DisposeCoreAsync(Task? startTask, Task queriesDrained)
    {
        await Task.Yield();
        ExceptionDispatchInfo? failure = null;
        if (startTask is not null)
        {
            await startTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        try
        {
            await _sampler.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= ExceptionDispatchInfo.Capture(exception);
        }

        await queriesDrained.ConfigureAwait(false);

        try
        {
            await _symbolResolver.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= ExceptionDispatchInfo.Capture(exception);
        }

        try
        {
            await _sampler.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= ExceptionDispatchInfo.Capture(exception);
        }

        try
        {
            await _store.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= ExceptionDispatchInfo.Capture(exception);
        }

        lock (_stateLock)
        {
            _state = ExecutionSamplingSessionState.Disposed;
        }

        failure?.Throw();
    }

    private void ExitQuery()
    {
        TaskCompletionSource? queriesDrained = null;
        lock (_stateLock)
        {
            _activeQueryCount--;
            if (_state is ExecutionSamplingSessionState.Disposing && _activeQueryCount == 0)
            {
                queriesDrained = _queriesDrained;
            }
        }

        queriesDrained?.TrySetResult();
    }

    private static ExecutionSamplingComponents CreateComponents(
        string? storageRootDirectory,
        ILogger<ExecutionSymbolResolver>? symbolLogger)
    {
        var layout = new ExecutionCaptureStorageLayout(storageRootDirectory);
        var store = new ExecutionCaptureStore(layout);
        var symbolResolver = new ExecutionSymbolResolver(logger: symbolLogger);
        return new ExecutionSamplingComponents(
            store,
            new EventPipeExecutionSampler(store),
            symbolResolver);
    }

    private static bool IsTransientStartFailure(Exception exception) =>
        ContainsStartFailure(
            exception,
            static candidate => candidate is DiagnosticsClientException or IOException);

    private static bool IsMappedStartFailure(Exception exception) =>
        ContainsStartFailure(
            exception,
            static candidate => candidate is DiagnosticsClientException or IOException or UnauthorizedAccessException);

    private static bool ContainsStartFailure(Exception exception, Func<Exception, bool> predicate)
    {
        if (predicate(exception))
        {
            return true;
        }

        return exception is AggregateException aggregateException
            && aggregateException.InnerExceptions.Any(innerException =>
                ContainsStartFailure(innerException, predicate));
    }

    private static DiagnosticsException CreateRangeUnavailableException() =>
        new(
            DiagnosticsErrorCode.ExecutionProfileRangeUnavailable,
            "The requested execution profile range is not available in this session.");

    private sealed record ExecutionSamplingComponents(
        ExecutionCaptureStore Store,
        IEventPipeExecutionSampler Sampler,
        ExecutionSymbolResolver SymbolResolver);

    private enum ExecutionSamplingSessionState
    {
        Created,
        Starting,
        Running,
        Unavailable,
        Disposing,
        Disposed
    }
}
