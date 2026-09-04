using System.Threading.Channels;
using DotnetAnalysis.Core.Events;
using Microsoft.Extensions.Logging;

namespace DotnetAnalysis.Application.Events;

/// <summary>
/// 基于有界 Channel 的进程内异步事件总线。
/// </summary>
public sealed class InProcessEventBus : IEventBus
{
    private readonly object _gate = new();
    private readonly ILogger<InProcessEventBus> _logger;
    private readonly List<IEventSubscription> _subscriptions = [];
    private readonly List<IEventSubscription> _retiredSubscriptions = [];
    private readonly TimeSpan _shutdownTimeout;
    private readonly Action<ILogger, string, Exception?> _shutdownTimedOut;
    private bool _disposed;

    /// <summary>
    /// 创建基于有界队列的进程内事件总线。
    /// </summary>
    /// <param name="logger">记录处理器失败及关闭超时的日志记录器。</param>
    /// <param name="shutdownTimeout">关闭时等待订阅完成的最长时间。</param>
    public InProcessEventBus(ILogger<InProcessEventBus> logger, TimeSpan? shutdownTimeout = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(1);
        if (_shutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(shutdownTimeout), "Shutdown timeout must be greater than zero.");
        }

        _shutdownTimedOut = LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2, "EventSubscriptionShutdownTimedOut"),
            "Event subscription {SubscriptionIdentity} did not complete before the shutdown timeout.");
    }

    /// <summary>
    /// 将事件提交到所有匹配订阅；队列满时返回投递异常。
    /// </summary>
    /// <typeparam name="TEvent">要发布的事件类型。</typeparam>
    /// <param name="applicationEvent">不可为空的事件实例。</param>
    /// <param name="cancellationToken">发布前检查的取消令牌。</param>
    public ValueTask PublishAsync<TEvent>(TEvent applicationEvent, CancellationToken cancellationToken)
        where TEvent : IApplicationEvent
    {
        ArgumentNullException.ThrowIfNull(applicationEvent);
        cancellationToken.ThrowIfCancellationRequested();

        IEventSubscription[] subscriptions;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            subscriptions = _subscriptions
                .Where(subscription => subscription.EventType == typeof(TEvent))
                .ToArray();
        }

        EventDeliveryException? firstDeliveryFailure = null;
        foreach (var subscription in subscriptions)
        {
            var deliveryFailure = subscription.TryEnqueue(applicationEvent);
            firstDeliveryFailure ??= deliveryFailure;
        }

        return firstDeliveryFailure is null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(firstDeliveryFailure);
    }

    /// <summary>
    /// 注册异步处理器并返回可释放订阅句柄。
    /// </summary>
    /// <typeparam name="TEvent">要处理的事件类型。</typeparam>
    /// <param name="handler">接收事件和处理取消令牌的处理器。</param>
    /// <param name="options">队列容量配置；省略时使用默认值。</param>
    /// <returns>释放后停止接收新事件的订阅。</returns>
    public IDisposable Subscribe<TEvent>(
        Func<TEvent, CancellationToken, ValueTask> handler,
        EventSubscriptionOptions? options = null)
        where TEvent : IApplicationEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        var effectiveOptions = options ?? new EventSubscriptionOptions();
        if (effectiveOptions.QueueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                effectiveOptions.QueueCapacity,
                "Queue capacity must be greater than zero.");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var subscription = new EventSubscription<TEvent>(
                handler,
                effectiveOptions,
                _logger,
                RetireSubscription,
                ReleaseRetiredSubscription,
                PublishFaultAsync);
            _subscriptions.Add(subscription);
            return subscription;
        }
    }

    /// <summary>
    /// 停止接纳事件、取消处理器并等待订阅在限定时间内退出。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        IEventSubscription[] subscriptions;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            subscriptions = [.. _subscriptions, .. _retiredSubscriptions];
            _subscriptions.Clear();
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Dispose();
            subscription.RequestHandlerCancellation();
        }

        var completion = Task.WhenAll(subscriptions.Select(subscription => subscription.Completion));
        if (await Task.WhenAny(completion, Task.Delay(_shutdownTimeout)).ConfigureAwait(false) == completion)
        {
            await completion.ConfigureAwait(false);
            return;
        }

        foreach (var subscription in subscriptions.Where(subscription => !subscription.Completion.IsCompleted))
        {
            _shutdownTimedOut(_logger, subscription.Identity, null);
        }
    }

    /// <summary>
    /// 把处理器故障事件重新交给总线投递。
    /// </summary>
    private ValueTask PublishFaultAsync(ModuleFaulted fault)
    {
        return PublishAsync(fault, CancellationToken.None);
    }

    /// <summary>
    /// 从活跃订阅集合移除已释放但仍在消费的订阅。
    /// </summary>
    private void RetireSubscription(IEventSubscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
            if (!_retiredSubscriptions.Contains(subscription))
            {
                _retiredSubscriptions.Add(subscription);
            }
        }
    }

    /// <summary>
    /// 在订阅消费循环结束后移除其退休记录。
    /// </summary>
    private void ReleaseRetiredSubscription(IEventSubscription subscription)
    {
        lock (_gate)
        {
            _retiredSubscriptions.Remove(subscription);
        }
    }

    /// <summary>
    /// 描述事件总线用于管理单个订阅生命周期的内部契约。
    /// </summary>
    private interface IEventSubscription : IDisposable
    {
        /// <summary>
        /// 订阅消费循环的完成任务。
        /// </summary>
        Task Completion { get; }

        /// <summary>
        /// 订阅匹配的事件运行时类型。
        /// </summary>
        Type EventType { get; }

        /// <summary>
        /// 用于日志和故障事件的订阅唯一标识。
        /// </summary>
        string Identity { get; }

        /// <summary>
        /// 尝试把事件加入订阅队列。
        /// </summary>
        EventDeliveryException? TryEnqueue(IApplicationEvent applicationEvent);

        /// <summary>
        /// 请求当前处理器尽快取消。
        /// </summary>
        void RequestHandlerCancellation();
    }

    /// <summary>
    /// 为单一事件类型维护有界队列和串行消费循环的订阅实现。
    /// </summary>
    private sealed class EventSubscription<TEvent> : IEventSubscription
        where TEvent : IApplicationEvent
    {
        private readonly CancellationTokenSource _handlerCancellation = new();
        private readonly object _admissionGate = new();
        private readonly Channel<SubscriptionWorkItem> _queue;
        private readonly Func<TEvent, CancellationToken, ValueTask> _handler;
        private readonly ILogger _logger;
        private readonly Action<IEventSubscription> _retire;
        private readonly Action<IEventSubscription> _release;
        private readonly Func<ModuleFaulted, ValueTask> _publishFault;
        private readonly Dictionary<string, TEvent> _pendingLatestOnly = [];
        private readonly HashSet<string> _latestOnlyMarkers = [];
        private readonly Action<ILogger, string, Exception?> _handlerFailed;
        private readonly Action<ILogger, Exception?> _faultPublicationFailed;
        private readonly string _identity;
        private bool _accepting = true;

        /// <summary>
        /// 创建订阅并立即启动其后台消费循环。
        /// </summary>
        /// <param name="handler">事件处理器。</param>
        /// <param name="options">队列容量和投递配置。</param>
        /// <param name="logger">处理失败日志记录器。</param>
        /// <param name="retire">将订阅从活跃集合移出的回调。</param>
        /// <param name="release">消费循环结束后的清理回调。</param>
        /// <param name="publishFault">发布处理器故障事件的回调。</param>
        public EventSubscription(
            Func<TEvent, CancellationToken, ValueTask> handler,
            EventSubscriptionOptions options,
            ILogger logger,
            Action<IEventSubscription> retire,
            Action<IEventSubscription> release,
            Func<ModuleFaulted, ValueTask> publishFault)
        {
            _handler = handler;
            _logger = logger;
            _retire = retire;
            _release = release;
            _publishFault = publishFault;
            _identity = $"{typeof(TEvent).FullName}/{Guid.NewGuid():N}";
            _handlerFailed = LoggerMessage.Define<string>(
                LogLevel.Error,
                new EventId(1, "EventHandlerFailed"),
                "Event handler failed for {EventType}.");
            _faultPublicationFailed = LoggerMessage.Define(
                LogLevel.Error,
                new EventId(3, "ModuleFaultedDeliveryFailed"),
                "Could not publish ModuleFaulted after an event handler failure.");
            _queue = Channel.CreateBounded<SubscriptionWorkItem>(new BoundedChannelOptions(options.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            Completion = ConsumeAsync();
        }

        /// <summary>
        /// 获取订阅消费循环的完成任务。
        /// </summary>
        public Task Completion { get; }

        /// <summary>
        /// 获取订阅处理的事件类型。
        /// </summary>
        public Type EventType => typeof(TEvent);

        /// <summary>
        /// 获取订阅的稳定日志标识。
        /// </summary>
        public string Identity => _identity;

        /// <summary>
        /// 按投递策略尝试接纳一个事件。
        /// </summary>
        public EventDeliveryException? TryEnqueue(IApplicationEvent applicationEvent)
        {
            lock (_admissionGate)
            {
                if (!_accepting)
                {
                    return null;
                }

                if (applicationEvent is IApplicationEventDeliveryPolicy
                    {
                        DeliveryMode: ApplicationEventDeliveryMode.LatestOnly
                    } policy)
                {
                    return TryEnqueueLatestOnly((TEvent)applicationEvent, policy.DeliveryKey);
                }

                if (applicationEvent is IApplicationEventDeliveryPolicy
                    {
                        DeliveryMode: not ApplicationEventDeliveryMode.Ordered
                    })
                {
                    throw new InvalidOperationException(
                        $"Unsupported application event delivery mode for {applicationEvent.GetType().Name}.");
                }

                return TryEnqueueOrdered((TEvent)applicationEvent);
            }
        }

        /// <summary>
        /// 按顺序把事件写入有界队列。
        /// </summary>
        private EventDeliveryException? TryEnqueueOrdered(TEvent applicationEvent)
        {
            return _queue.Writer.TryWrite(new EventWorkItem(applicationEvent))
                ? null
                : new EventDeliveryException(applicationEvent.GetType(), Identity);
        }

        /// <summary>
        /// 合并同一键的待处理事件，只保留最新值。
        /// </summary>
        private EventDeliveryException? TryEnqueueLatestOnly(TEvent applicationEvent, string deliveryKey)
        {
            if (string.IsNullOrWhiteSpace(deliveryKey))
            {
                throw new InvalidOperationException(
                    $"LatestOnly event {typeof(TEvent).Name} must provide a non-empty delivery key.");
            }

            if (_latestOnlyMarkers.Contains(deliveryKey))
            {
                _pendingLatestOnly[deliveryKey] = applicationEvent;
                return null;
            }

            if (_queue.Writer.TryWrite(new LatestOnlyWorkItem(deliveryKey, applicationEvent)))
            {
                _latestOnlyMarkers.Add(deliveryKey);
                return null;
            }

            _pendingLatestOnly[deliveryKey] = applicationEvent;
            TrySchedulePendingLatestOnly();

            return null;
        }

        /// <summary>
        /// 从工作项取出事件，并安排合并队列中的后续事件。
        /// </summary>
        private bool TryGetEvent(SubscriptionWorkItem workItem, out TEvent applicationEvent)
        {
            if (workItem is EventWorkItem eventWorkItem)
            {
                applicationEvent = eventWorkItem.Event;
                lock (_admissionGate)
                {
                    TrySchedulePendingLatestOnly();
                }

                return true;
            }

            var latestOnlyWorkItem = (LatestOnlyWorkItem)workItem;
            lock (_admissionGate)
            {
                _latestOnlyMarkers.Remove(latestOnlyWorkItem.DeliveryKey);
                if (_pendingLatestOnly.Remove(latestOnlyWorkItem.DeliveryKey, out var latestEvent))
                {
                    applicationEvent = latestEvent;
                    TrySchedulePendingLatestOnly();
                    return true;
                }

                applicationEvent = latestOnlyWorkItem.InitialEvent;
                TrySchedulePendingLatestOnly();
                return true;
            }
        }

        /// <summary>
        /// 在队列有空间时安排尚未投递的最新事件。
        /// </summary>
        private void TrySchedulePendingLatestOnly()
        {
            foreach (var (deliveryKey, latestEvent) in _pendingLatestOnly)
            {
                if (_latestOnlyMarkers.Contains(deliveryKey))
                {
                    continue;
                }

                if (!_queue.Writer.TryWrite(new LatestOnlyWorkItem(deliveryKey, latestEvent)))
                {
                    return;
                }

                _latestOnlyMarkers.Add(deliveryKey);
            }
        }

        /// <summary>
        /// 停止接纳新事件并完成队列写入端。
        /// </summary>
        public void Dispose()
        {
            lock (_admissionGate)
            {
                if (!_accepting)
                {
                    return;
                }

                _accepting = false;
            }

            _retire(this);
            _queue.Writer.TryComplete();
        }

        /// <summary>
        /// 取消处理器令牌，促使长时间运行的处理器退出。
        /// </summary>
        public void RequestHandlerCancellation()
        {
            try
            {
                _handlerCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// 串行消费队列并隔离处理器异常。
        /// </summary>
        private async Task ConsumeAsync()
        {
            try
            {
                await foreach (var workItem in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    if (!TryGetEvent(workItem, out var applicationEvent))
                    {
                        continue;
                    }

                    try
                    {
                        await _handler(applicationEvent, _handlerCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_handlerCancellation.IsCancellationRequested)
                    {
                    }
                    catch (Exception exception)
                    {
                        _handlerFailed(_logger, typeof(TEvent).Name, exception);
                        if (applicationEvent is not ModuleFaulted)
                        {
                            await PublishHandlerFaultAsync(applicationEvent, exception).ConfigureAwait(false);
                        }
                    }
                }
            }
            finally
            {
                _handlerCancellation.Dispose();
                _release(this);
            }
        }

        /// <summary>
        /// 把处理器异常转换为模块故障事件并尽力发布。
        /// </summary>
        private async ValueTask PublishHandlerFaultAsync(TEvent applicationEvent, Exception exception)
        {
            try
            {
                await _publishFault(new ModuleFaulted(
                    applicationEvent.SessionId,
                    typeof(TEvent).Name,
                    exception.Message,
                    DateTimeOffset.UtcNow,
                    nameof(InProcessEventBus))).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (EventDeliveryException deliveryException)
            {
                _faultPublicationFailed(_logger, deliveryException);
            }
        }

        /// <summary>
        /// 订阅消费循环处理的工作项基类。
        /// </summary>
        private abstract record SubscriptionWorkItem;

        /// <summary>
        /// 表示必须按顺序处理的普通事件工作项。
        /// </summary>
        private sealed record EventWorkItem(TEvent Event) : SubscriptionWorkItem;

        /// <summary>
        /// 表示按投递键合并的最新值工作项。
        /// </summary>
        private sealed record LatestOnlyWorkItem(string DeliveryKey, TEvent InitialEvent) : SubscriptionWorkItem;
    }
}
