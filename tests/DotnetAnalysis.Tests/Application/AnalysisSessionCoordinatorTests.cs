using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Application.Contracts;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Application.Sessions;
using DotnetAnalysis.Core.Events;
using DotnetAnalysis.Core.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetAnalysis.Tests.Application;

[TestClass]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names describe the behavior under test.")]
public sealed class AnalysisSessionCoordinatorTests
{
    private static readonly string[] NormalCompletionCalls = ["start", "stop", "snapshot", "analyze"];
    private static readonly string[] CancellationCalls = ["start", "cancel"];
    private static readonly string[] CancellationDuringFinishCalls = ["start", "stop", "cancel"];
    private static readonly string[] StopFailureCalls = ["start", "stop"];
    private static readonly string[] StartOnlyCalls = ["start"];

    [TestMethod]
    public async Task FinishAsync_StopsTraceBeforeSnapshotBeforeAnalysis()
    {
        var calls = new List<string>();
        var backend = new RecordingCaptureBackend(calls);
        var analyzer = new RecordingAnalysisService(calls);
        await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var captureStarted = new TaskCompletionSource<CaptureStarted>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<AnalysisCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var captureSubscription = bus.Subscribe<CaptureStarted>((@event, _) =>
        {
            captureStarted.TrySetResult(@event);
            return ValueTask.CompletedTask;
        });
        using var completionSubscription = bus.Subscribe<AnalysisCompleted>((@event, _) =>
        {
            completed.TrySetResult(@event);
            return ValueTask.CompletedTask;
        });
        var coordinator = new AnalysisSessionCoordinator(backend, analyzer, bus, TimeProvider.System);

        var session = await coordinator.StartAsync(CancellationToken.None);
        await coordinator.FinishAsync(session.Id, CancellationToken.None);

        CollectionAssert.AreEqual(NormalCompletionCalls, calls);
        Assert.AreEqual(AnalysisSessionState.Completed, session.State);
        Assert.AreEqual(session.Id, (await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(1))).SessionId);
        Assert.AreEqual(session.Id, (await completed.Task.WaitAsync(TimeSpan.FromSeconds(1))).SessionId);
    }

    [TestMethod]
    public async Task CancelAsync_DoesNotCaptureSnapshotOrAnalyze()
    {
        var calls = new List<string>();
        await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var canceled = new TaskCompletionSource<AnalysisCanceled>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationSubscription = bus.Subscribe<AnalysisCanceled>((@event, _) =>
        {
            canceled.TrySetResult(@event);
            return ValueTask.CompletedTask;
        });
        var coordinator = new AnalysisSessionCoordinator(
            new RecordingCaptureBackend(calls),
            new RecordingAnalysisService(calls),
            bus,
            TimeProvider.System);

        var session = await coordinator.StartAsync(CancellationToken.None);
        await coordinator.CancelAsync(session.Id, CancellationToken.None);

        CollectionAssert.AreEqual(CancellationCalls, calls);
        Assert.AreEqual(AnalysisSessionState.Canceled, session.State);
        Assert.AreEqual(session.Id, (await canceled.Task.WaitAsync(TimeSpan.FromSeconds(1))).SessionId);
    }

    [TestMethod]
    public async Task CancelAsync_DuringFinish_SkipsSnapshotAndAnalysis()
    {
        var calls = new List<string>();
        var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var coordinator = new AnalysisSessionCoordinator(
            new BlockingStopCaptureBackend(calls, stopStarted, releaseStop),
            new RecordingAnalysisService(calls),
            bus,
            TimeProvider.System);

        var session = await coordinator.StartAsync(CancellationToken.None);
        var finish = coordinator.FinishAsync(session.Id, CancellationToken.None);
        await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var cancellation = coordinator.CancelAsync(session.Id, CancellationToken.None);
        releaseStop.TrySetResult();

        await Task.WhenAll(finish, cancellation);

        CollectionAssert.AreEqual(CancellationDuringFinishCalls, calls);
        Assert.AreEqual(AnalysisSessionState.Canceled, session.State);
    }

    [TestMethod]
    public async Task FinishAsync_WhenStoppingTraceFails_PublishesFailureWithoutStartingLaterStages()
    {
        var calls = new List<string>();
        await using var bus = new RecordingEventBus();
        var coordinator = new AnalysisSessionCoordinator(
            new ThrowingStopCaptureBackend(calls),
            new RecordingAnalysisService(calls),
            bus,
            TimeProvider.System);

        var session = await coordinator.StartAsync(CancellationToken.None);
        await coordinator.FinishAsync(session.Id, CancellationToken.None);

        CollectionAssert.AreEqual(StopFailureCalls, calls);
        Assert.AreEqual(AnalysisSessionState.Failed, session.State);
        var failure = bus.Events.OfType<AnalysisFailed>().Single();
        Assert.AreEqual("analysis_session_failed", failure.FailureCode);
        Assert.AreEqual("The memory analysis session could not be completed.", failure.Message);
    }

    [TestMethod]
    public async Task CancelAsync_WhenBackendFails_TransitionsToFailedAndPublishesFailure()
    {
        var calls = new List<string>();
        await using var bus = new RecordingEventBus();
        var coordinator = new AnalysisSessionCoordinator(
            new ThrowingCancelCaptureBackend(calls),
            new RecordingAnalysisService(calls),
            bus,
            TimeProvider.System);

        var session = await coordinator.StartAsync(CancellationToken.None);
        await coordinator.CancelAsync(session.Id, CancellationToken.None);

        CollectionAssert.AreEqual(CancellationCalls, calls);
        Assert.AreEqual(AnalysisSessionState.Failed, session.State);
        Assert.HasCount(1, bus.Events.OfType<AnalysisFailed>());
    }

    [TestMethod]
    public async Task StartAsync_WhenCaptureStartedDeliveryFails_TransitionsToFailedWithoutLeakingBusException()
    {
        var calls = new List<string>();
        await using var bus = new RecordingEventBus(failCaptureStarted: true);
        var coordinator = new AnalysisSessionCoordinator(
            new RecordingCaptureBackend(calls),
            new RecordingAnalysisService(calls),
            bus,
            TimeProvider.System);

        var session = await coordinator.StartAsync(CancellationToken.None);

        CollectionAssert.AreEqual(StartOnlyCalls, calls);
        Assert.AreEqual(AnalysisSessionState.Failed, session.State);
        Assert.HasCount(1, bus.Events.OfType<AnalysisFailed>());
    }

    [TestMethod]
    public async Task FinishAsync_WhenAlreadyFinishing_ReturnsTheInFlightTask()
    {
        var calls = new List<string>();
        var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var coordinator = new AnalysisSessionCoordinator(
            new BlockingStopCaptureBackend(calls, stopStarted, releaseStop),
            new RecordingAnalysisService(calls),
            bus,
            TimeProvider.System);

        var session = await coordinator.StartAsync(CancellationToken.None);
        var first = coordinator.FinishAsync(session.Id, CancellationToken.None);
        await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var repeated = coordinator.FinishAsync(session.Id, CancellationToken.None);
        releaseStop.TrySetResult();

        Assert.AreSame(first, repeated);
        await Task.WhenAll(first, repeated);
        CollectionAssert.AreEqual(NormalCompletionCalls, calls);
    }

    [TestMethod]
    public async Task CancelAsync_WhenAlreadyCanceling_ReturnsTheInFlightTask()
    {
        var calls = new List<string>();
        var cancelStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var coordinator = new AnalysisSessionCoordinator(
            new BlockingCancelCaptureBackend(calls, cancelStarted, releaseCancel),
            new RecordingAnalysisService(calls),
            bus,
            TimeProvider.System);

        var session = await coordinator.StartAsync(CancellationToken.None);
        var first = coordinator.CancelAsync(session.Id, CancellationToken.None);
        await cancelStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var repeated = coordinator.CancelAsync(session.Id, CancellationToken.None);
        releaseCancel.TrySetResult();

        Assert.AreSame(first, repeated);
        await Task.WhenAll(first, repeated);
        CollectionAssert.AreEqual(CancellationCalls, calls);
    }

    private sealed class RecordingCaptureBackend(List<string> calls) : ICaptureBackend
    {
        public Task StartAllocationTraceAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            calls.Add("start");
            return Task.CompletedTask;
        }

        public Task StopAllocationTraceAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            calls.Add("stop");
            return Task.CompletedTask;
        }

        public Task CaptureHeapSnapshotAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            calls.Add("snapshot");
            return Task.CompletedTask;
        }

        public Task CancelAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            calls.Add("cancel");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAnalysisService(List<string> calls) : IAnalysisService
    {
        public Task AnalyzeAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            calls.Add("analyze");
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingStopCaptureBackend(
        List<string> calls,
        TaskCompletionSource stopStarted,
        TaskCompletionSource releaseStop) : ICaptureBackend
    {
        public Task StartAllocationTraceAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            calls.Add("start");
            return Task.CompletedTask;
        }

        public async Task StopAllocationTraceAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            calls.Add("stop");
            stopStarted.TrySetResult();
            await releaseStop.Task.ConfigureAwait(false);
        }

        public Task CaptureHeapSnapshotAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            calls.Add("snapshot");
            return Task.CompletedTask;
        }

        public Task CancelAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            calls.Add("cancel");
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingStopCaptureBackend(List<string> calls) : ICaptureBackend
    {
        public Task StartAllocationTraceAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            calls.Add("start");
            return Task.CompletedTask;
        }

        public Task StopAllocationTraceAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            calls.Add("stop");
            throw new InvalidOperationException("Stop failed.");
        }

        public Task CaptureHeapSnapshotAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            calls.Add("snapshot");
            return Task.CompletedTask;
        }

        public Task CancelAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            calls.Add("cancel");
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingCancelCaptureBackend(
        List<string> calls,
        TaskCompletionSource cancelStarted,
        TaskCompletionSource releaseCancel) : ICaptureBackend
    {
        public Task StartAllocationTraceAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            calls.Add("start");
            return Task.CompletedTask;
        }

        public Task StopAllocationTraceAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            calls.Add("stop");
            return Task.CompletedTask;
        }

        public Task CaptureHeapSnapshotAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            calls.Add("snapshot");
            return Task.CompletedTask;
        }

        public async Task CancelAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            calls.Add("cancel");
            cancelStarted.TrySetResult();
            await releaseCancel.Task.ConfigureAwait(false);
        }
    }

    private sealed class ThrowingCancelCaptureBackend(List<string> calls) : ICaptureBackend
    {
        public Task StartAllocationTraceAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            calls.Add("start");
            return Task.CompletedTask;
        }

        public Task StopAllocationTraceAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            calls.Add("stop");
            return Task.CompletedTask;
        }

        public Task CaptureHeapSnapshotAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            calls.Add("snapshot");
            return Task.CompletedTask;
        }

        public Task CancelAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            calls.Add("cancel");
            throw new InvalidOperationException("Cancellation failed.");
        }
    }

    private sealed class RecordingEventBus(bool failCaptureStarted = false) : IEventBus
    {
        public List<IApplicationEvent> Events { get; } = [];

        public ValueTask PublishAsync<TEvent>(TEvent applicationEvent, CancellationToken cancellationToken)
            where TEvent : IApplicationEvent
        {
            Events.Add(applicationEvent);
            return failCaptureStarted && applicationEvent is CaptureStarted
                ? ValueTask.FromException(new EventDeliveryException(typeof(CaptureStarted), "test-subscription"))
                : ValueTask.CompletedTask;
        }

        public IDisposable Subscribe<TEvent>(
            Func<TEvent, CancellationToken, ValueTask> handler,
            EventSubscriptionOptions? options = null)
            where TEvent : IApplicationEvent
        {
            return NoOpDisposable.Instance;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public static NoOpDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
