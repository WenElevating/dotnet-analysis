using System.Runtime.ExceptionServices;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Extensions.Logging;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 定义附着会话内执行采样的启动、查询和异步释放边界。
/// </summary>
internal interface IExecutionSamplingSession : IAsyncDisposable
{
    /// <summary>
    /// 启动目标进程的执行采样；启动状态会决定后续查询是可用还是返回稳定不可用错误。
    /// </summary>
    Task StartAsync(TargetProcess target, CancellationToken cancellationToken);

    /// <summary>
    /// 使用默认增量模式构建指定时间范围的执行分析结果。
    /// </summary>
    Task<ExecutionProfile> GetExecutionProfileAsync(
        ExecutionTimeRange timeRange,
        CancellationToken cancellationToken);

    /// <summary>
    /// 使用调用方指定的读取模式构建指定时间范围的执行分析结果。
    /// </summary>
    Task<ExecutionProfile> GetExecutionProfileAsync(
        ExecutionTimeRange timeRange,
        ExecutionProfileQueryMode queryMode,
        CancellationToken cancellationToken) => GetExecutionProfileAsync(timeRange, cancellationToken);
}

/// <summary>
/// 拥有附着期执行采样、连续查询和会话私有资源的生命周期。
/// </summary>
internal sealed class ExecutionSamplingSession : IExecutionSamplingSession
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    private const int CompletedIncrementalProfileCacheCapacity = 64;
    private const int MaximumConcurrentIncrementalProfileBuilds = 2;

    private readonly object _stateLock = new();
    private readonly ExecutionCaptureStore _store;
    private readonly IEventPipeExecutionSampler _sampler;
    private readonly ExecutionSymbolResolver _symbolResolver;
    private readonly ExecutionProfileBuilder _profileBuilder;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _retryDelayAsync;
    private readonly CancellationTokenSource _sessionLifetimeCancellation = new();
    private readonly SemaphoreSlim _incrementalProfileBuildGate = new(
        MaximumConcurrentIncrementalProfileBuilds,
        MaximumConcurrentIncrementalProfileBuilds);

    private ExecutionSamplingSessionState _state;
    private DateTimeOffset _samplingStartedAtUtc;
    private DiagnosticsException? _unavailableFailure;
    private Task? _startTask;
    private Task? _disposeTask;
    private int _activeQueryCount;
    private TaskCompletionSource? _queriesDrained;
    private readonly Dictionary<ExecutionProfileQueryCacheKey, Task<ExecutionProfile>> _incrementalProfileCache = [];
    private readonly LinkedList<ExecutionProfileQueryCacheKey> _completedIncrementalProfileCacheLru = [];
    private readonly Dictionary<ExecutionProfileQueryCacheKey, LinkedListNode<ExecutionProfileQueryCacheKey>>
        _completedIncrementalProfileCacheLruNodes = [];

    /// <summary>
    /// 使用默认会话存储、EventPipe 读取器和本地符号解析器创建执行采样会话。
    /// </summary>
    /// <summary>
    /// 为测试或组合根注入会话独占组件、时钟和延迟策略，以验证启动、取消和缓存生命周期。
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
        if (!Enum.IsDefined(queryMode))
        {
            throw new ArgumentOutOfRangeException(nameof(queryMode), queryMode, "Execution profile query mode is not supported.");
        }

        ExecutionCaptureReadBoundary boundary;
        long lostEventCount;
        Task<ExecutionProfile>? incrementalProfileTask = null;
        ExecutionProfileQueryCacheKey? incrementalProfileCacheKeyToBuild = null;
        TaskCompletionSource<ExecutionProfile>? incrementalProfileCompletionToBuild = null;
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
            if (queryMode is ExecutionProfileQueryMode.FullScan)
            {
                _activeQueryCount++;
            }
            else
            {
                var rangeDependency = ExecutionCaptureStore.GetRangeDependency(timeRange, boundary);
                var cacheKey = new ExecutionProfileQueryCacheKey(
                    timeRange,
                    boundary,
                    rangeDependency,
                    lostEventCount);
                if (_incrementalProfileCache.TryGetValue(cacheKey, out incrementalProfileTask))
                {
                    RefreshCompletedIncrementalProfileRecency(cacheKey);
                }
                else
                {
                    var completion = new TaskCompletionSource<ExecutionProfile>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    incrementalProfileTask = completion.Task;
                    _incrementalProfileCache.Add(cacheKey, incrementalProfileTask);
                    _activeQueryCount++;
                    incrementalProfileCacheKeyToBuild = cacheKey;
                    incrementalProfileCompletionToBuild = completion;
                }
            }
        }

        if (incrementalProfileCompletionToBuild is not null)
        {
            _ = CompleteIncrementalProfileBuildAsync(
                incrementalProfileCacheKeyToBuild!,
                incrementalProfileCompletionToBuild,
                timeRange,
                boundary,
                lostEventCount);
        }

        if (incrementalProfileTask is not null)
        {
            return await incrementalProfileTask.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _sessionLifetimeCancellation.Cancel();
            _queriesDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_activeQueryCount == 0)
            {
                _queriesDrained.TrySetResult();
            }

            _disposeTask = DisposeCoreAsync(_startTask, _queriesDrained.Task);
            return new ValueTask(_disposeTask);
        }
    }

    /// <summary>
    /// 创建采样组件并启动 EventPipe；仅对已识别的短暂传输失败进行一次受控重试。
    /// </summary>
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

    /// <summary>
    /// 先取消共享查询和输入处理，再等待启动、查询、采样器与临时存储按安全顺序收敛。
    /// </summary>
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

        lock (_stateLock)
        {
            _incrementalProfileCache.Clear();
            _completedIncrementalProfileCacheLru.Clear();
            _completedIncrementalProfileCacheLruNodes.Clear();
        }

        _incrementalProfileBuildGate.Dispose();
        _sessionLifetimeCancellation.Dispose();

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

    /// <summary>
    /// 注销一个查询等待者；最后一个退出者释放处置流程等待的查询排空信号。
    /// </summary>
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

    /// <summary>
    /// 在会话私有并发预算内完成一个不同键的增量构建，并无论成功、失败或会话取消都清理单飞项。
    /// </summary>
    private async Task CompleteIncrementalProfileBuildAsync(
        ExecutionProfileQueryCacheKey cacheKey,
        TaskCompletionSource<ExecutionProfile> completion,
        ExecutionTimeRange timeRange,
        ExecutionCaptureReadBoundary boundary,
        long lostEventCount)
    {
        var enteredBuildGate = false;
        try
        {
            await _incrementalProfileBuildGate.WaitAsync(_sessionLifetimeCancellation.Token).ConfigureAwait(false);
            enteredBuildGate = true;
            var profile = await _profileBuilder.BuildIncrementalAsync(
                timeRange,
                boundary,
                lostEventCount,
                _sessionLifetimeCancellation.Token).ConfigureAwait(false);
            lock (_stateLock)
            {
                _sessionLifetimeCancellation.Token.ThrowIfCancellationRequested();
                if (IsCachedIncrementalProfileTask(cacheKey, completion.Task))
                {
                    TrackCompletedIncrementalProfile(cacheKey);
                }

                completion.TrySetResult(profile);
            }
        }
        catch (OperationCanceledException) when (_sessionLifetimeCancellation.IsCancellationRequested)
        {
            lock (_stateLock)
            {
                RemoveCachedIncrementalProfile(cacheKey, completion.Task);
            }

            completion.TrySetCanceled(_sessionLifetimeCancellation.Token);
        }
        catch (Exception exception)
        {
            lock (_stateLock)
            {
                RemoveCachedIncrementalProfile(cacheKey, completion.Task);
            }

            completion.TrySetException(exception);
        }
        finally
        {
            if (enteredBuildGate)
            {
                _incrementalProfileBuildGate.Release();
            }

            ExitQuery();
        }
    }

    /// <summary>
    /// 判断字典当前项仍是指定任务，避免旧失败或旧完成回调误删后来重建的同键单飞项。
    /// </summary>
    private bool IsCachedIncrementalProfileTask(
        ExecutionProfileQueryCacheKey cacheKey,
        Task<ExecutionProfile> task) => _incrementalProfileCache.TryGetValue(cacheKey, out var cachedTask)
            && ReferenceEquals(cachedTask, task);

    /// <summary>
    /// 将已命中的完成结果移动到 LRU 末尾；运行或排队任务不参与容量淘汰。
    /// </summary>
    private void RefreshCompletedIncrementalProfileRecency(ExecutionProfileQueryCacheKey cacheKey)
    {
        if (!_completedIncrementalProfileCacheLruNodes.TryGetValue(cacheKey, out var existingNode))
        {
            return;
        }

        _completedIncrementalProfileCacheLru.Remove(existingNode);
        _completedIncrementalProfileCacheLru.AddLast(existingNode);
    }

    /// <summary>
    /// 登记成功完成的增量结果并将 LRU 缓存限制在既定容量，绝不驱逐运行中的单飞任务。
    /// </summary>
    private void TrackCompletedIncrementalProfile(ExecutionProfileQueryCacheKey cacheKey)
    {
        if (_completedIncrementalProfileCacheLruNodes.ContainsKey(cacheKey))
        {
            RefreshCompletedIncrementalProfileRecency(cacheKey);
            return;
        }

        var addedNode = _completedIncrementalProfileCacheLru.AddLast(cacheKey);
        _completedIncrementalProfileCacheLruNodes.Add(cacheKey, addedNode);
        if (_completedIncrementalProfileCacheLruNodes.Count <= CompletedIncrementalProfileCacheCapacity)
        {
            return;
        }

        var leastRecentlyUsedNode = _completedIncrementalProfileCacheLru.First!;
        _completedIncrementalProfileCacheLru.RemoveFirst();
        _completedIncrementalProfileCacheLruNodes.Remove(leastRecentlyUsedNode.Value);
        _incrementalProfileCache.Remove(leastRecentlyUsedNode.Value);
    }

    /// <summary>
    /// 仅在当前项仍属于指定任务时移除缓存，保证失败或会话取消后相同范围可以安全重建。
    /// </summary>
    private void RemoveCachedIncrementalProfile(
        ExecutionProfileQueryCacheKey cacheKey,
        Task<ExecutionProfile> task)
    {
        if (!IsCachedIncrementalProfileTask(cacheKey, task))
        {
            return;
        }

        _incrementalProfileCache.Remove(cacheKey);
        if (_completedIncrementalProfileCacheLruNodes.Remove(cacheKey, out var completedNode))
        {
            _completedIncrementalProfileCacheLru.Remove(completedNode);
        }
    }

    /// <summary>
    /// 集中创建相互协作的采样器、持久化存储、聚合器与符号解析器，保持会话资源所有权清晰。
    /// </summary>
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

    /// <summary>
    /// 判断启动失败是否属于允许一次延迟重试的短暂诊断传输故障。
    /// </summary>
    private static bool IsTransientStartFailure(Exception exception) =>
        ContainsStartFailure(
            exception,
            static candidate => candidate is DiagnosticsClientException or IOException);

    /// <summary>
    /// 判断启动异常是否应映射为稳定的执行采样不可用错误。
    /// </summary>
    private static bool IsMappedStartFailure(Exception exception) =>
        ContainsStartFailure(
            exception,
            static candidate => candidate is DiagnosticsClientException or IOException or UnauthorizedAccessException);

    /// <summary>
    /// 遍历聚合异常树，查找满足指定启动失败分类谓词的内部异常。
    /// </summary>
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

    /// <summary>
    /// 为已超出会话可查询水位的时间范围创建稳定诊断错误。
    /// </summary>
    private static DiagnosticsException CreateRangeUnavailableException() =>
        new(
            DiagnosticsErrorCode.ExecutionProfileRangeUnavailable,
            "The requested execution profile range is not available in this session.");

    /// <summary>
    /// 聚合一个会话独占的采样实现组件，便于启动失败时统一释放已创建资源。
    /// </summary>
    private sealed record ExecutionSamplingComponents(
        ExecutionCaptureStore Store,
        IEventPipeExecutionSampler Sampler,
        ExecutionSymbolResolver SymbolResolver);

    /// <summary>
    /// 表示增量查询的稳定缓存键：封存历史使用段版本，活动范围使用完整读取边界，且始终包含丢失事件数。
    /// </summary>
    private sealed class ExecutionProfileQueryCacheKey : IEquatable<ExecutionProfileQueryCacheKey>
    {
        private readonly ExecutionCaptureSealedSegmentVersion[]? _sealedSegmentVersions;

        /// <summary>
        /// 从固定读取边界和范围依赖快照构建缓存键；封存历史不保留与结果无关的活动段水位。
        /// </summary>
        public ExecutionProfileQueryCacheKey(
            ExecutionTimeRange timeRange,
            ExecutionCaptureReadBoundary boundary,
            ExecutionCaptureRangeDependency rangeDependency,
            long lostEventCount)
        {
            ArgumentNullException.ThrowIfNull(timeRange);
            ArgumentNullException.ThrowIfNull(boundary);
            ArgumentNullException.ThrowIfNull(rangeDependency);
            ArgumentOutOfRangeException.ThrowIfNegative(lostEventCount);

            TimeRange = timeRange;
            LostEventCount = lostEventCount;
            if (rangeDependency.IsSealedHistoryOnly)
            {
                _sealedSegmentVersions = rangeDependency.SealedSegmentVersions.ToArray();
            }
            else
            {
                StartedAtUtc = boundary.StartedAtUtc;
                WrittenThroughUtc = boundary.WrittenThroughUtc;
                LastCompletedRecord = boundary.LastCompletedRecord;
            }
        }

        private ExecutionTimeRange TimeRange { get; }

        private DateTimeOffset StartedAtUtc { get; }

        private DateTimeOffset WrittenThroughUtc { get; }

        private long LastCompletedRecord { get; }

        private long LostEventCount { get; }

        /// <summary>
        /// 比较所有决定执行分析结果的时间范围、读取依赖和丢失事件快照。
        /// </summary>
        public bool Equals(ExecutionProfileQueryCacheKey? other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            if (other is null
                || !TimeRange.Equals(other.TimeRange)
                || LostEventCount != other.LostEventCount
                || (_sealedSegmentVersions is null) != (other._sealedSegmentVersions is null))
            {
                return false;
            }

            if (_sealedSegmentVersions is null)
            {
                return StartedAtUtc == other.StartedAtUtc
                    && WrittenThroughUtc == other.WrittenThroughUtc
                    && LastCompletedRecord == other.LastCompletedRecord;
            }

            return _sealedSegmentVersions.AsSpan().SequenceEqual(other._sealedSegmentVersions);
        }

        /// <summary>
        /// 将对象比较转发到强类型键比较，供字典和 LRU 索引使用。
        /// </summary>
        public override bool Equals(object? obj) => Equals(obj as ExecutionProfileQueryCacheKey);

        /// <summary>
        /// 组合与强类型相等比较完全一致的字段，避免不同结果范围共享缓存项。
        /// </summary>
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(TimeRange);
            hash.Add(LostEventCount);
            hash.Add(_sealedSegmentVersions is not null);
            if (_sealedSegmentVersions is null)
            {
                hash.Add(StartedAtUtc);
                hash.Add(WrittenThroughUtc);
                hash.Add(LastCompletedRecord);
            }
            else
            {
                foreach (var segmentVersion in _sealedSegmentVersions)
                {
                    hash.Add(segmentVersion);
                }
            }

            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// 表示执行采样会话的启动、可查询和处置终态，用于拒绝不合法生命周期调用。
    /// </summary>
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
