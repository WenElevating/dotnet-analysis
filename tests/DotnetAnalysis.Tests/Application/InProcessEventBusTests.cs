using System.Diagnostics.CodeAnalysis;
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
        var subscription = bus.Subscribe<CaptureStarted>((_, _) =>
        {
            calls++;
            return ValueTask.CompletedTask;
        });

        subscription.Dispose();
        await bus.PublishAsync(
            new CaptureStarted(SessionId.New(), DateTimeOffset.UtcNow, "Coordinator"),
            CancellationToken.None);
        await Task.Delay(50);

        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task PublishAsync_CoalescesProgressToLatestValueForSlowSubscriber()
    {
        await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<int>();
        using var subscription = bus.Subscribe<CaptureProgressChanged>(async (@event, _) =>
        {
            await gate.Task;
            received.Add(@event.Percent);
        });

        var sessionId = SessionId.New();
        for (var percent = 1; percent <= 100; percent++)
        {
            await bus.PublishAsync(
                new CaptureProgressChanged(sessionId, percent, DateTimeOffset.UtcNow, "Capture"),
                CancellationToken.None);
        }

        gate.SetResult();
        await Task.Delay(100);
        Assert.AreEqual(100, received[^1]);
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
        var faultDeliveries = 0;
        using var throwingFaultSubscription = bus.Subscribe<ModuleFaulted>((_, _) =>
        {
            throw new InvalidOperationException("Fault handler failure.");
        });
        using var healthyFaultSubscription = bus.Subscribe<ModuleFaulted>((_, _) =>
        {
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
        await Task.Delay(100);

        Assert.AreEqual(1, Volatile.Read(ref faultDeliveries));
    }

    [TestMethod]
    public async Task DisposeAsync_WaitsForRunningSubscriber()
    {
        var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = bus.Subscribe<CaptureStarted>(async (_, _) =>
        {
            handlerStarted.TrySetResult();
            await releaseHandler.Task;
        });

        await bus.PublishAsync(new CaptureStarted(SessionId.New(), DateTimeOffset.UtcNow, "Coordinator"), CancellationToken.None);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var disposal = bus.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.IsFalse(disposal.IsCompleted);

        releaseHandler.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
