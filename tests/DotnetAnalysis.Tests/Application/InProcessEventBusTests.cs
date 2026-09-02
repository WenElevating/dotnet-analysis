using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Core.Sessions;
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
        var received = new TaskCompletionSource<CaptureStarted>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = bus.Subscribe<CaptureStarted>((@event, _) =>
        {
            received.TrySetResult(@event);
            return ValueTask.CompletedTask;
        });

        var expected = new CaptureStarted(SessionId.New(), DateTimeOffset.UtcNow, "Coordinator");
        await bus.PublishAsync(expected, CancellationToken.None);

        Assert.AreEqual(expected, await received.Task.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [TestMethod]
    public async Task DisposedSubscription_DoesNotReceiveSubsequentEvents()
    {
        await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var calls = 0;
        var healthyReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = bus.Subscribe<CaptureStarted>((_, _) =>
        {
            calls++;
            return ValueTask.CompletedTask;
        });
        using var healthySubscription = bus.Subscribe<CaptureStarted>((_, _) =>
        {
            healthyReceived.TrySetResult();
            return ValueTask.CompletedTask;
        });

        subscription.Dispose();
        await bus.PublishAsync(
            new CaptureStarted(SessionId.New(), DateTimeOffset.UtcNow, "Coordinator"),
            CancellationToken.None);
        await healthyReceived.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task PublishAsync_FailsFastWhenLifecycleQueueIsFullAndHealthySubscriberStillReceives()
    {
        await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var slowHandlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlowHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthyReceived = new TaskCompletionSource<CaptureStarted>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var slowSubscription = bus.Subscribe<CaptureStarted>(async (_, _) =>
        {
            slowHandlerStarted.TrySetResult();
            await releaseSlowHandler.Task;
        }, new EventSubscriptionOptions(QueueCapacity: 1));
        using var healthySubscription = bus.Subscribe<CaptureStarted>((@event, _) =>
        {
            if (@event.Source == "third")
            {
                healthyReceived.TrySetResult(@event);
            }

            return ValueTask.CompletedTask;
        });

        try
        {
            await bus.PublishAsync(new CaptureStarted(SessionId.New(), DateTimeOffset.UtcNow, "first"), CancellationToken.None);
            await slowHandlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await bus.PublishAsync(new CaptureStarted(SessionId.New(), DateTimeOffset.UtcNow, "second"), CancellationToken.None);
            var expected = new CaptureStarted(SessionId.New(), DateTimeOffset.UtcNow, "third");

            var exception = await Assert.ThrowsAsync<EventDeliveryException>(async () =>
                await bus.PublishAsync(expected, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromMilliseconds(250)));

            Assert.AreEqual(typeof(CaptureStarted), exception.EventType);
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
        using var subscription = bus.Subscribe<CaptureStarted>(async (@event, _) =>
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

        await bus.PublishAsync(new CaptureStarted(SessionId.New(), DateTimeOffset.UtcNow, "first"), CancellationToken.None);
        await firstHandlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await bus.PublishAsync(new CaptureStarted(SessionId.New(), DateTimeOffset.UtcNow, "second"), CancellationToken.None);
        subscription.Dispose();

        releaseFirstHandler.TrySetResult();
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.HasCount(2, received);
        Assert.AreEqual("first", received[0]);
        Assert.AreEqual("second", received[1]);
    }

    [TestMethod]
    public async Task PublishAsync_CoalescesProgressToExactFirstAndLatestValuesForSlowSubscriber()
    {
        await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<int>();
        var firstHandlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = bus.Subscribe<CaptureProgressChanged>(async (@event, _) =>
        {
            received.Add(@event.Percent);
            if (@event.Percent == 1)
            {
                firstHandlerStarted.TrySetResult();
                await gate.Task;
            }
            else if (@event.Percent == 100)
            {
                latestReceived.TrySetResult();
            }
        });

        var sessionId = SessionId.New();
        await bus.PublishAsync(
            new CaptureProgressChanged(sessionId, 1, DateTimeOffset.UtcNow, "Capture"),
            CancellationToken.None);
        await firstHandlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        for (var percent = 2; percent <= 100; percent++)
        {
            await bus.PublishAsync(
                new CaptureProgressChanged(sessionId, percent, DateTimeOffset.UtcNow, "Capture"),
                CancellationToken.None);
        }

        gate.SetResult();
        await latestReceived.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.HasCount(2, received);
        Assert.AreEqual(1, received[0]);
        Assert.AreEqual(100, received[1]);
    }

    [TestMethod]
    public async Task PublishAsync_CoalescesProgressIndependentlyPerSession()
    {
        await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstHandlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allExpectedEventsReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<(SessionId SessionId, int Percent)>();
        var firstSessionId = SessionId.New();
        var secondSessionId = SessionId.New();
        using var subscription = bus.Subscribe<CaptureProgressChanged>(async (@event, _) =>
        {
            received.Add((@event.SessionId!.Value, @event.Percent));
            if (@event.SessionId == firstSessionId && @event.Percent == 1)
            {
                firstHandlerStarted.TrySetResult();
                await gate.Task;
            }

            if (received.Count == 3)
            {
                allExpectedEventsReceived.TrySetResult();
            }
        });

        await bus.PublishAsync(
            new CaptureProgressChanged(firstSessionId, 1, DateTimeOffset.UtcNow, "Capture"),
            CancellationToken.None);
        await firstHandlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await bus.PublishAsync(
            new CaptureProgressChanged(firstSessionId, 2, DateTimeOffset.UtcNow, "Capture"),
            CancellationToken.None);
        await bus.PublishAsync(
            new CaptureProgressChanged(secondSessionId, 10, DateTimeOffset.UtcNow, "Capture"),
            CancellationToken.None);
        await bus.PublishAsync(
            new CaptureProgressChanged(secondSessionId, 20, DateTimeOffset.UtcNow, "Capture"),
            CancellationToken.None);

        gate.SetResult();
        await allExpectedEventsReceived.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.HasCount(3, received);
        Assert.AreEqual((firstSessionId, 1), received[0]);
        Assert.AreEqual((firstSessionId, 2), received[1]);
        Assert.AreEqual((secondSessionId, 20), received[2]);
    }

    [TestMethod]
    public async Task HandlerFailure_PublishesFaultWithoutInterruptingHealthySubscriber()
    {
        await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var healthyReceived = new TaskCompletionSource<CaptureStarted>(TaskCreationOptions.RunContinuationsAsynchronously);
        var faultReceived = new TaskCompletionSource<ModuleFaulted>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var faultSubscription = bus.Subscribe<ModuleFaulted>((@event, _) =>
        {
            faultReceived.TrySetResult(@event);
            return ValueTask.CompletedTask;
        });
        using var throwingSubscription = bus.Subscribe<CaptureStarted>((_, _) =>
        {
            throw new InvalidOperationException("Handler failure.");
        });
        using var healthySubscription = bus.Subscribe<CaptureStarted>((@event, _) =>
        {
            healthyReceived.TrySetResult(@event);
            return ValueTask.CompletedTask;
        });

        var expected = new CaptureStarted(SessionId.New(), DateTimeOffset.UtcNow, "Coordinator");
        await bus.PublishAsync(expected, CancellationToken.None);

        Assert.AreEqual(expected, await healthyReceived.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        var fault = await faultReceived.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(expected.SessionId, fault.SessionId);
        Assert.AreEqual(nameof(CaptureStarted), fault.Module);
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
        using var throwingSubscription = bus.Subscribe<CaptureStarted>((_, _) =>
        {
            throw new InvalidOperationException("Capture handler failure.");
        });

        await bus.PublishAsync(
            new CaptureStarted(SessionId.New(), DateTimeOffset.UtcNow, "Coordinator"),
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
        var bus = new InProcessEventBus(
            NullLogger<InProcessEventBus>.Instance,
            TimeSpan.FromMilliseconds(50));
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = bus.Subscribe<CaptureStarted>(async (_, _) =>
        {
            handlerStarted.TrySetResult();
            await releaseHandler.Task;
        });

        await bus.PublishAsync(new CaptureStarted(SessionId.New(), DateTimeOffset.UtcNow, "Coordinator"), CancellationToken.None);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var stopwatch = Stopwatch.StartNew();
        var disposal = bus.DisposeAsync().AsTask();
        await disposal.WaitAsync(TimeSpan.FromSeconds(1));
        stopwatch.Stop();
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1));

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
        using var subscription = bus.Subscribe<CaptureStarted>(async (_, _) =>
        {
            handlerStarted.TrySetResult();
            await releaseHandler.Task;
        });

        await bus.PublishAsync(new CaptureStarted(SessionId.New(), DateTimeOffset.UtcNow, "Coordinator"), CancellationToken.None);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        subscription.Dispose();

        var disposal = bus.DisposeAsync().AsTask();
        Assert.IsFalse(disposal.IsCompleted);

        releaseHandler.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
