using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetAnalysis.Tests.Application;

[TestClass]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names describe the behavior under test.")]
public sealed class InProcessEventBusTests
{
    [TestMethod]
    public async Task PublishAsync_DeliversLifecycleEventToMatchingSubscriber()
    {
        await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var received = new TaskCompletionSource<MemorySnapshotCaptureStarted>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = bus.Subscribe<MemorySnapshotCaptureStarted>((@event, _) =>
        {
            received.TrySetResult(@event);
            return ValueTask.CompletedTask;
        });

        var expected = new MemorySnapshotCaptureStarted(ProcessDiagnosticsSessionId.New(), DateTimeOffset.UtcNow, "Coordinator");
        await bus.PublishAsync(expected, CancellationToken.None);

        Assert.AreEqual(expected, await received.Task.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [TestMethod]
    public async Task DisposedSubscription_DoesNotReceiveSubsequentEvents()
    {
        await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var calls = 0;
        var healthyReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = bus.Subscribe<MemorySnapshotCaptureStarted>((_, _) =>
        {
            calls++;
            return ValueTask.CompletedTask;
        });
        using var healthySubscription = bus.Subscribe<MemorySnapshotCaptureStarted>((_, _) =>
        {
            healthyReceived.TrySetResult();
            return ValueTask.CompletedTask;
        });

        subscription.Dispose();
        await bus.PublishAsync(
            new MemorySnapshotCaptureStarted(ProcessDiagnosticsSessionId.New(), DateTimeOffset.UtcNow, "Coordinator"),
            CancellationToken.None);
        await healthyReceived.Task.WaitAsync(TimeSpan.FromSeconds(1));
        healthySubscription.Dispose();
        await bus.DisposeAsync();

        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task PublishAsync_FailsFastWhenLifecycleQueueIsFullAndHealthySubscriberStillReceives()
    {
        await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var slowHandlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlowHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthyReceived = new TaskCompletionSource<MemorySnapshotCaptureStarted>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var slowSubscription = bus.Subscribe<MemorySnapshotCaptureStarted>(async (_, _) =>
        {
            slowHandlerStarted.TrySetResult();
            await releaseSlowHandler.Task;
        }, new EventSubscriptionOptions(QueueCapacity: 1));
        using var healthySubscription = bus.Subscribe<MemorySnapshotCaptureStarted>((@event, _) =>
        {
            if (@event.Source == "third")
            {
                healthyReceived.TrySetResult(@event);
            }

            return ValueTask.CompletedTask;
        });

        try
        {
            await bus.PublishAsync(new MemorySnapshotCaptureStarted(ProcessDiagnosticsSessionId.New(), DateTimeOffset.UtcNow, "first"), CancellationToken.None);
            await slowHandlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await bus.PublishAsync(new MemorySnapshotCaptureStarted(ProcessDiagnosticsSessionId.New(), DateTimeOffset.UtcNow, "second"), CancellationToken.None);
            var expected = new MemorySnapshotCaptureStarted(ProcessDiagnosticsSessionId.New(), DateTimeOffset.UtcNow, "third");

            var exception = await Assert.ThrowsAsync<EventDeliveryException>(async () =>
                await bus.PublishAsync(expected, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromMilliseconds(250)));

            Assert.AreEqual(typeof(MemorySnapshotCaptureStarted), exception.EventType);
            Assert.IsFalse(string.IsNullOrWhiteSpace(exception.SubscriptionIdentity));
            Assert.AreEqual(expected, await healthyReceived.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            releaseSlowHandler.TrySetResult();
        }
    }

    [TestMethod]
    public async Task DisposedSubscription_DrainsLifecycleEventsAlreadyAdmitted()
    {
        await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var firstHandlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<string>();
        using var subscription = bus.Subscribe<MemorySnapshotCaptureStarted>(async (@event, _) =>
        {
            received.Add(@event.Source);
            if (@event.Source == "first")
            {
                firstHandlerStarted.TrySetResult();
                await releaseFirstHandler.Task;
            }
            else
            {
                drained.TrySetResult();
            }
        }, new EventSubscriptionOptions(QueueCapacity: 2));

        await bus.PublishAsync(new MemorySnapshotCaptureStarted(ProcessDiagnosticsSessionId.New(), DateTimeOffset.UtcNow, "first"), CancellationToken.None);
        await firstHandlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await bus.PublishAsync(new MemorySnapshotCaptureStarted(ProcessDiagnosticsSessionId.New(), DateTimeOffset.UtcNow, "second"), CancellationToken.None);
        subscription.Dispose();

        releaseFirstHandler.TrySetResult();
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.HasCount(2, received);
        Assert.AreEqual("first", received[0]);
        Assert.AreEqual("second", received[1]);
    }

    [TestMethod]
    public async Task LatestOnly_SameKeyDeliversNewestPendingSample()
    {
        await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<long>();
        var firstHandlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = bus.Subscribe<ProcessMemoryUsageUpdated>(async (@event, _) =>
        {
            received.Add(@event.Sample.ProcessMemoryBytes!.Value);
            if (@event.Sample.ProcessMemoryBytes == 1)
            {
                firstHandlerStarted.TrySetResult();
                await gate.Task;
            }
            else if (@event.Sample.ProcessMemoryBytes == 100)
            {
                latestReceived.TrySetResult();
            }
        });

        var sessionId = ProcessDiagnosticsSessionId.New();
        await bus.PublishAsync(ProcessMemoryUsageUpdated(sessionId, 1), CancellationToken.None);
        await firstHandlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var stopwatch = Stopwatch.StartNew();
        for (var value = 2; value <= 100; value++)
        {
            await bus.PublishAsync(
                ProcessMemoryUsageUpdated(sessionId, value),
                CancellationToken.None);
        }
        stopwatch.Stop();
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250));

        gate.SetResult();
        await latestReceived.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.HasCount(2, received);
        Assert.AreEqual(1, received[0]);
        Assert.AreEqual(100, received[1]);
    }

    [TestMethod]
    public async Task LatestOnly_DifferentKeysCoalesceIndependently()
    {
        await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstHandlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allExpectedEventsReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSessionId = ProcessDiagnosticsSessionId.New();
        var secondSessionId = ProcessDiagnosticsSessionId.New();
        var received = new List<(ProcessDiagnosticsSessionId SessionId, long Bytes)>();
        using var subscription = bus.Subscribe<ProcessMemoryUsageUpdated>(async (@event, _) =>
        {
            received.Add((@event.SessionId, @event.Sample.ProcessMemoryBytes!.Value));
            if (@event.SessionId == firstSessionId && @event.Sample.ProcessMemoryBytes == 1)
            {
                firstHandlerStarted.TrySetResult();
                await gate.Task;
            }

            if (received.Count == 3)
            {
                allExpectedEventsReceived.TrySetResult();
            }
        }, new EventSubscriptionOptions(QueueCapacity: 1));

        await bus.PublishAsync(ProcessMemoryUsageUpdated(firstSessionId, 1), CancellationToken.None);
        await firstHandlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await bus.PublishAsync(ProcessMemoryUsageUpdated(firstSessionId, 2), CancellationToken.None);
        await bus.PublishAsync(ProcessMemoryUsageUpdated(secondSessionId, 10), CancellationToken.None);
        await bus.PublishAsync(ProcessMemoryUsageUpdated(secondSessionId, 20), CancellationToken.None);

        gate.SetResult();
        await allExpectedEventsReceived.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.HasCount(3, received);
        Assert.AreEqual((firstSessionId, 1), received[0]);
        Assert.AreEqual((firstSessionId, 2), received[1]);
        Assert.AreEqual((secondSessionId, 20), received[2]);
    }

    [TestMethod]
    public async Task Ordered_WhenQueueIsFull_ReportsDeliveryFailure()
    {
        await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = bus.Subscribe<MemorySnapshotCaptureStarted>(async (_, _) =>
        {
            handlerStarted.TrySetResult();
            await releaseHandler.Task;
        }, new EventSubscriptionOptions(QueueCapacity: 1));

        await bus.PublishAsync(new MemorySnapshotCaptureStarted(ProcessDiagnosticsSessionId.New(), DateTimeOffset.UtcNow, "first"), CancellationToken.None);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await bus.PublishAsync(new MemorySnapshotCaptureStarted(ProcessDiagnosticsSessionId.New(), DateTimeOffset.UtcNow, "second"), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<EventDeliveryException>(async () =>
            await bus.PublishAsync(new MemorySnapshotCaptureStarted(ProcessDiagnosticsSessionId.New(), DateTimeOffset.UtcNow, "third"), CancellationToken.None).AsTask());

        Assert.AreEqual(typeof(MemorySnapshotCaptureStarted), exception.EventType);
        Assert.IsFalse(string.IsNullOrWhiteSpace(exception.SubscriptionIdentity));
        releaseHandler.TrySetResult();
    }

    [TestMethod]
    public async Task HandlerFailure_PublishesFaultWithoutInterruptingHealthySubscriber()
    {
        await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var healthyReceived = new TaskCompletionSource<MemorySnapshotCaptureStarted>(TaskCreationOptions.RunContinuationsAsynchronously);
        var faultReceived = new TaskCompletionSource<ModuleFaulted>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var faultSubscription = bus.Subscribe<ModuleFaulted>((@event, _) =>
        {
            faultReceived.TrySetResult(@event);
            return ValueTask.CompletedTask;
        });
        using var throwingSubscription = bus.Subscribe<MemorySnapshotCaptureStarted>((_, _) =>
        {
            throw new InvalidOperationException("Handler failure.");
        });
        using var healthySubscription = bus.Subscribe<MemorySnapshotCaptureStarted>((@event, _) =>
        {
            healthyReceived.TrySetResult(@event);
            return ValueTask.CompletedTask;
        });

        var expected = new MemorySnapshotCaptureStarted(ProcessDiagnosticsSessionId.New(), DateTimeOffset.UtcNow, "Coordinator");
        await bus.PublishAsync(expected, CancellationToken.None);

        Assert.AreEqual(expected, await healthyReceived.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        var fault = await faultReceived.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(expected.SessionId, fault.SessionId);
        Assert.AreEqual(nameof(MemorySnapshotCaptureStarted), fault.Module);
    }

    [TestMethod]
    public async Task ModuleFaultedHandlerFailure_DoesNotPublishRecursiveFault()
    {
        await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var faultReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstBarrierProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBarrierProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var faultDeliveries = 0;
        using var throwingFaultSubscription = bus.Subscribe<ModuleFaulted>((@event, _) =>
        {
            if (@event.Source == "barrier-1")
            {
                firstBarrierProcessed.TrySetResult();
                return ValueTask.CompletedTask;
            }

            if (@event.Source == "barrier-2")
            {
                return ValueTask.CompletedTask;
            }

            throw new InvalidOperationException("Fault handler failure.");
        });
        using var healthyFaultSubscription = bus.Subscribe<ModuleFaulted>((@event, _) =>
        {
            if (@event.Source == "barrier-2")
            {
                secondBarrierProcessed.TrySetResult();
                return ValueTask.CompletedTask;
            }

            if (@event.Source == "barrier-1")
            {
                return ValueTask.CompletedTask;
            }

            Interlocked.Increment(ref faultDeliveries);
            faultReceived.TrySetResult();
            return ValueTask.CompletedTask;
        });
        using var throwingSubscription = bus.Subscribe<MemorySnapshotCaptureStarted>((_, _) =>
        {
            throw new InvalidOperationException("Capture handler failure.");
        });

        await bus.PublishAsync(
            new MemorySnapshotCaptureStarted(ProcessDiagnosticsSessionId.New(), DateTimeOffset.UtcNow, "Coordinator"),
            CancellationToken.None);
        await faultReceived.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await bus.PublishAsync(
            new ModuleFaulted(null, "barrier", "barrier", DateTimeOffset.UtcNow, "barrier-1"),
            CancellationToken.None);
        await firstBarrierProcessed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await bus.PublishAsync(
            new ModuleFaulted(null, "barrier", "barrier", DateTimeOffset.UtcNow, "barrier-2"),
            CancellationToken.None);
        await secondBarrierProcessed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(1, Volatile.Read(ref faultDeliveries));
    }

    [TestMethod]
    public async Task DisposeAsync_CompletesAfterBoundedWaitForNonCooperativeSubscriber()
    {
        var logger = new RecordingLogger<InProcessEventBus>();
        var bus = new InProcessEventBus(logger, TimeSpan.FromMilliseconds(50));
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = bus.Subscribe<MemorySnapshotCaptureStarted>(async (_, _) =>
        {
            handlerStarted.TrySetResult();
            await releaseHandler.Task;
        });

        await bus.PublishAsync(new MemorySnapshotCaptureStarted(ProcessDiagnosticsSessionId.New(), DateTimeOffset.UtcNow, "Coordinator"), CancellationToken.None);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var stopwatch = Stopwatch.StartNew();
        var disposal = bus.DisposeAsync().AsTask();
        await disposal.WaitAsync(TimeSpan.FromSeconds(1));
        stopwatch.Stop();
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.IsTrue(logger.Entries.Any(entry =>
            entry.LogLevel == LogLevel.Warning
            && entry.EventId.Name == "EventSubscriptionShutdownTimedOut"));

        releaseHandler.TrySetResult();
    }

    [TestMethod]
    public async Task DisposeAsync_WaitsForRetiredSubscriptionUntilItsConsumerCompletes()
    {
        var bus = new InProcessEventBus(
            NullLogger<InProcessEventBus>.Instance,
            TimeSpan.FromSeconds(1));
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = bus.Subscribe<MemorySnapshotCaptureStarted>(async (_, _) =>
        {
            handlerStarted.TrySetResult();
            await releaseHandler.Task;
        });

        await bus.PublishAsync(new MemorySnapshotCaptureStarted(ProcessDiagnosticsSessionId.New(), DateTimeOffset.UtcNow, "Coordinator"), CancellationToken.None);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        subscription.Dispose();

        var disposal = bus.DisposeAsync().AsTask();
        Assert.IsFalse(disposal.IsCompleted);

        releaseHandler.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static ProcessMemoryUsageUpdated ProcessMemoryUsageUpdated(
        ProcessDiagnosticsSessionId sessionId,
        long processMemoryBytes) =>
        new(
            sessionId,
            new MemoryUsageSample(
                DateTimeOffset.UtcNow,
                managedHeapBytes: processMemoryBytes,
                processMemoryBytes: processMemoryBytes,
            MemoryUsageSampleState.Measured),
            DateTimeOffset.UtcNow,
            "test");

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add(new LogEntry(logLevel, eventId, exception));
        }
    }

    private sealed record LogEntry(
        LogLevel LogLevel,
        EventId EventId,
        Exception? Exception);
}
