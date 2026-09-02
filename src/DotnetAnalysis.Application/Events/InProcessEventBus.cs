using System.Threading.Channels;
using DotnetAnalysis.Core.Events;
using Microsoft.Extensions.Logging;

namespace DotnetAnalysis.Application.Events;

public sealed class InProcessEventBus : IEventBus
{
    private readonly object _gate = new();
    private readonly ILogger<InProcessEventBus> _logger;
    private readonly List<IEventSubscription> _subscriptions = [];
    private readonly List<IEventSubscription> _retiredSubscriptions = [];
    private readonly TimeSpan _shutdownTimeout;
    private readonly Action<ILogger, string, Exception?> _shutdownTimedOut;
    private bool _disposed;

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

    private ValueTask PublishFaultAsync(ModuleFaulted fault)
    {
        return PublishAsync(fault, CancellationToken.None);
    }

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

    private void ReleaseRetiredSubscription(IEventSubscription subscription)
    {
        lock (_gate)
        {
            _retiredSubscriptions.Remove(subscription);
        }
    }

    private interface IEventSubscription : IDisposable
    {
        Task Completion { get; }

        Type EventType { get; }

        string Identity { get; }

        EventDeliveryException? TryEnqueue(IApplicationEvent applicationEvent);

        void RequestHandlerCancellation();
    }

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

        public Task Completion { get; }

        public Type EventType => typeof(TEvent);

        public string Identity => _identity;

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

        private EventDeliveryException? TryEnqueueOrdered(TEvent applicationEvent)
        {
            return _queue.Writer.TryWrite(new EventWorkItem(applicationEvent))
                ? null
                : new EventDeliveryException(applicationEvent.GetType(), Identity);
        }

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

        private abstract record SubscriptionWorkItem;

        private sealed record EventWorkItem(TEvent Event) : SubscriptionWorkItem;

        private sealed record LatestOnlyWorkItem(string DeliveryKey, TEvent InitialEvent) : SubscriptionWorkItem;
    }
}
