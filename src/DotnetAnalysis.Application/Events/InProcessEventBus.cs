using System.Threading.Channels;
using DotnetAnalysis.Core.Events;
using Microsoft.Extensions.Logging;

namespace DotnetAnalysis.Application.Events;

public sealed class InProcessEventBus : IEventBus
{
    private readonly object _gate = new();
    private readonly ILogger<InProcessEventBus> _logger;
    private readonly List<IEventSubscription> _subscriptions = [];
    private bool _disposed;

    public InProcessEventBus(ILogger<InProcessEventBus> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask PublishAsync<TEvent>(TEvent applicationEvent, CancellationToken cancellationToken)
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

        await Task.WhenAll(subscriptions
            .Select(subscription => subscription.EnqueueAsync(applicationEvent, cancellationToken).AsTask()))
            .ConfigureAwait(false);
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
                RemoveSubscription,
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
            subscriptions = [.. _subscriptions];
            _subscriptions.Clear();
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Dispose();
        }

        await Task.WhenAll(subscriptions.Select(subscription => subscription.Completion)).ConfigureAwait(false);
    }

    private ValueTask PublishFaultAsync(ModuleFaulted fault)
    {
        return PublishAsync(fault, CancellationToken.None);
    }

    private void RemoveSubscription(IEventSubscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
        }
    }

    private interface IEventSubscription : IDisposable
    {
        Task Completion { get; }

        Type EventType { get; }

        ValueTask EnqueueAsync(IApplicationEvent @event, CancellationToken cancellationToken);
    }

    private sealed class EventSubscription<TEvent> : IEventSubscription
        where TEvent : IApplicationEvent
    {
        private readonly CancellationTokenSource _shutdown = new();
        private readonly object _progressGate = new();
        private readonly Channel<SubscriptionWorkItem> _queue;
        private readonly Func<TEvent, CancellationToken, ValueTask> _handler;
        private readonly ILogger _logger;
        private readonly Action<IEventSubscription> _remove;
        private readonly Func<ModuleFaulted, ValueTask> _publishFault;
        private readonly bool _coalesceProgressEvents;
        private readonly Dictionary<ProgressSessionKey, CaptureProgressChanged> _pendingProgress = [];
        private readonly Dictionary<ProgressSessionKey, Task> _progressMarkers = [];
        private readonly Action<ILogger, string, Exception?> _handlerFailed;
        private int _disposed;

        public EventSubscription(
            Func<TEvent, CancellationToken, ValueTask> handler,
            EventSubscriptionOptions options,
            ILogger logger,
            Action<IEventSubscription> remove,
            Func<ModuleFaulted, ValueTask> publishFault)
        {
            _handler = handler;
            _logger = logger;
            _remove = remove;
            _publishFault = publishFault;
            _coalesceProgressEvents = options.CoalesceProgressEvents;
            _handlerFailed = LoggerMessage.Define<string>(
                LogLevel.Error,
                new EventId(1, "EventHandlerFailed"),
                "Event handler failed for {EventType}.");
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

        public async ValueTask EnqueueAsync(IApplicationEvent applicationEvent, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (_coalesceProgressEvents && applicationEvent is CaptureProgressChanged progress)
            {
                await EnqueueCoalescedProgressAsync(progress, cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                await _queue.Writer.WriteAsync(
                    new EventWorkItem((TEvent)applicationEvent),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException) when (Volatile.Read(ref _disposed) != 0)
            {
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _remove(this);
            _shutdown.Cancel();
            _queue.Writer.TryComplete();
            lock (_progressGate)
            {
                _pendingProgress.Clear();
                _progressMarkers.Clear();
            }
        }

        private async Task ConsumeAsync()
        {
            try
            {
                await foreach (var workItem in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        return;
                    }

                    if (!TryGetEvent(workItem, out var applicationEvent))
                    {
                        continue;
                    }

                    try
                    {
                        await _handler(applicationEvent, _shutdown.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                    {
                        return;
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
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
            finally
            {
                _shutdown.Dispose();
            }
        }

        private async ValueTask EnqueueCoalescedProgressAsync(
            CaptureProgressChanged progress,
            CancellationToken cancellationToken)
        {
            var sessionKey = new ProgressSessionKey(progress.SessionId?.Value);
            Task markerWrite;
            lock (_progressGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                _pendingProgress[sessionKey] = progress;
                if (!_progressMarkers.TryGetValue(sessionKey, out markerWrite!))
                {
                    markerWrite = EnqueueProgressMarkerAsync(new ProgressWorkItem(sessionKey));
                    _progressMarkers.Add(sessionKey, markerWrite);
                }
            }

            await markerWrite.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task EnqueueProgressMarkerAsync(ProgressWorkItem marker)
        {
            try
            {
                await _queue.Writer.WriteAsync(marker, _shutdown.Token).ConfigureAwait(false);
            }
            catch (ChannelClosedException) when (Volatile.Read(ref _disposed) != 0)
            {
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
        }

        private bool TryGetEvent(SubscriptionWorkItem workItem, out TEvent applicationEvent)
        {
            if (workItem is EventWorkItem eventWorkItem)
            {
                applicationEvent = eventWorkItem.Event;
                return true;
            }

            var progressWorkItem = (ProgressWorkItem)workItem;
            lock (_progressGate)
            {
                _progressMarkers.Remove(progressWorkItem.SessionKey);
                if (_pendingProgress.Remove(progressWorkItem.SessionKey, out var progress))
                {
                    applicationEvent = (TEvent)(IApplicationEvent)progress;
                    return true;
                }
            }

            applicationEvent = default!;
            return false;
        }

        private abstract record SubscriptionWorkItem;

        private sealed record EventWorkItem(TEvent Event) : SubscriptionWorkItem;

        private sealed record ProgressWorkItem(ProgressSessionKey SessionKey) : SubscriptionWorkItem;

        private readonly record struct ProgressSessionKey(Guid? Value);

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
        }
    }
}
