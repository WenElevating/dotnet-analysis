using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Application.Contracts;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Application.Sessions;
using DotnetAnalysis.Core.Events;
using DotnetAnalysis.Core.Sessions;

namespace DotnetAnalysis.Tests.Application;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class AnalysisSessionCoordinatorTests
{
    private static readonly string[] NormalCompletionCalls = ["start", "stop", "snapshot", "analyze"];
    private static readonly string[] CancellationCalls = ["start", "cancel"];
    private static readonly string[] CancellationBeforeSnapshotCalls = ["start", "stop", "cancel"];
    private static readonly string[] CancellationBeforeAnalyzeCalls = ["start", "stop", "snapshot", "cancel"];
    private static readonly string[] CancellationAfterAnalyzeBackendCalls = ["start", "stop", "snapshot", "cancel"];
    private static readonly string[] CancellationAfterStageFailureCalls = ["start", "stop", "cancel"];
    private static readonly string[] StopFailureCalls = ["start", "stop"];
    private static readonly string[] AnalyzeCalls = ["analyze"];

    [TestMethod]
    public async Task FinishAsync_StopsTraceBeforeSnapshotBeforeAnalysis()
    {
        var backend = new ControlledCaptureBackend();
        var analyzer = new ControlledAnalysisService();
        await using var bus = new RecordingEventBus();
        var coordinator = CreateCoordinator(backend, analyzer, bus);

        var session = await coordinator.StartAsync(CancellationToken.None);
        await coordinator.FinishAsync(session.Id, CancellationToken.None);

        CollectionAssert.AreEqual(NormalCompletionCalls, backend.Calls.Concat(analyzer.Calls).ToArray());
        Assert.AreEqual(AnalysisSessionState.Completed, session.State);
        AssertBoundary(backend, "start", AnalysisSessionState.CapturingAllocations);
        AssertBoundary(backend, "stop", AnalysisSessionState.FinishingTrace);
        AssertBoundary(backend, "snapshot", AnalysisSessionState.CapturingHeapSnapshot);
        AssertBoundary(analyzer, "analyze", AnalysisSessionState.Analyzing);
        Assert.IsTrue(backend.Boundaries.All(boundary => boundary.TokenCanBeCanceled));
        Assert.IsTrue(analyzer.Boundaries.All(boundary => boundary.TokenCanBeCanceled));
        Assert.HasCount(1, bus.Events.OfType<CaptureStarted>());
        Assert.HasCount(1, bus.Events.OfType<AnalysisCompleted>());
    }

    [TestMethod]
    public async Task StartAsync_WhenCaptureStartedDeliveryFails_CancelsTraceBeforeFailingSession()
    {
        var backend = new ControlledCaptureBackend();
        await using var bus = new RecordingEventBus(failCaptureStarted: true);
        var coordinator = CreateCoordinator(backend, new ControlledAnalysisService(), bus);

        var session = await coordinator.StartAsync(CancellationToken.None);

        CollectionAssert.AreEqual(CancellationCalls, backend.Calls);
        Assert.AreEqual(AnalysisSessionState.Failed, session.State);
        AssertBoundary(backend, "cancel", AnalysisSessionState.Canceling);
        Assert.HasCount(1, bus.Events.OfType<AnalysisFailed>());
    }

    [TestMethod]
    public async Task CancelAsync_DoesNotCaptureSnapshotOrAnalyze()
    {
        var backend = new ControlledCaptureBackend();
        var analyzer = new ControlledAnalysisService();
        await using var bus = new RecordingEventBus();
        var coordinator = CreateCoordinator(backend, analyzer, bus);

        var session = await coordinator.StartAsync(CancellationToken.None);
        await coordinator.CancelAsync(session.Id, CancellationToken.None);

        CollectionAssert.AreEqual(CancellationCalls, backend.Calls);
        Assert.IsEmpty(analyzer.Calls);
        Assert.AreEqual(AnalysisSessionState.Canceled, session.State);
        AssertBoundary(backend, "cancel", AnalysisSessionState.Canceling);
        Assert.HasCount(1, bus.Events.OfType<AnalysisCanceled>());
    }

    [TestMethod]
    public async Task CancelAsync_DuringActiveStart_CancelsBackendAndReturnsDedicatedCancellationTask()
    {
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new ControlledCaptureBackend { StartGate = startGate };
        await using var bus = new RecordingEventBus();
        var coordinator = CreateCoordinator(backend, new ControlledAnalysisService(), bus);

        var start = coordinator.StartAsync(CancellationToken.None);
        await backend.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var session = backend.LastSession!;
        var cancellation = coordinator.CancelAsync(session.Id, CancellationToken.None);

        Assert.AreNotSame(start, cancellation);
        startGate.TrySetResult();
        await Task.WhenAll(start, cancellation);

        CollectionAssert.AreEqual(CancellationCalls, backend.Calls);
        Assert.AreEqual(AnalysisSessionState.Canceled, session.State);
        Assert.IsEmpty(bus.Events.OfType<CaptureStarted>());
    }

    [TestMethod]
    public async Task CancelAsync_ImmediatelyBeforeSnapshotAdmission_SkipsSnapshotAndAnalysis()
    {
        var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new ControlledCaptureBackend { StopGate = stopGate };
        var analyzer = new ControlledAnalysisService();
        await using var bus = new RecordingEventBus();
        var coordinator = CreateCoordinator(backend, analyzer, bus);

        var session = await coordinator.StartAsync(CancellationToken.None);
        var finish = coordinator.FinishAsync(session.Id, CancellationToken.None);
        await backend.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var cancellation = coordinator.CancelAsync(session.Id, CancellationToken.None);
        stopGate.TrySetResult();

        await Task.WhenAll(finish, cancellation);

        CollectionAssert.AreEqual(CancellationBeforeSnapshotCalls, backend.Calls);
        Assert.IsEmpty(analyzer.Calls);
        Assert.AreEqual(AnalysisSessionState.Canceled, session.State);
    }

    [TestMethod]
    public async Task CancelAsync_DuringFailingActiveFinish_StillInvokesBackendCancellation()
    {
        var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new ControlledCaptureBackend
        {
            StopGate = stopGate,
            StopException = new InvalidOperationException("Stop failed.")
        };
        await using var bus = new RecordingEventBus();
        var coordinator = CreateCoordinator(backend, new ControlledAnalysisService(), bus);

        var session = await coordinator.StartAsync(CancellationToken.None);
        var finish = coordinator.FinishAsync(session.Id, CancellationToken.None);
        await backend.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var cancellation = coordinator.CancelAsync(session.Id, CancellationToken.None);
        stopGate.TrySetResult();

        await Task.WhenAll(finish, cancellation);

        CollectionAssert.AreEqual(CancellationAfterStageFailureCalls, backend.Calls);
        Assert.AreEqual(AnalysisSessionState.Failed, session.State);
        Assert.HasCount(1, bus.Events.OfType<AnalysisFailed>());
    }

    [TestMethod]
    public async Task CancelAsync_ImmediatelyBeforeAnalyzeAdmission_SkipsAnalysisAndCompletion()
    {
        var snapshotGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new ControlledCaptureBackend { SnapshotGate = snapshotGate };
        var analyzer = new ControlledAnalysisService();
        await using var bus = new RecordingEventBus();
        var coordinator = CreateCoordinator(backend, analyzer, bus);

        var session = await coordinator.StartAsync(CancellationToken.None);
        var finish = coordinator.FinishAsync(session.Id, CancellationToken.None);
        await backend.SnapshotEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var cancellation = coordinator.CancelAsync(session.Id, CancellationToken.None);
        snapshotGate.TrySetResult();

        await Task.WhenAll(finish, cancellation);

        CollectionAssert.AreEqual(CancellationBeforeAnalyzeCalls, backend.Calls);
        Assert.IsEmpty(analyzer.Calls);
        Assert.AreEqual(AnalysisSessionState.Canceled, session.State);
        Assert.IsEmpty(bus.Events.OfType<AnalysisCompleted>());
    }

    [TestMethod]
    public async Task CancelAsync_ImmediatelyBeforeCompletionAdmission_SkipsCompletion()
    {
        var analyzeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new ControlledCaptureBackend();
        var analyzer = new ControlledAnalysisService { AnalyzeGate = analyzeGate };
        await using var bus = new RecordingEventBus();
        var coordinator = CreateCoordinator(backend, analyzer, bus);

        var session = await coordinator.StartAsync(CancellationToken.None);
        var finish = coordinator.FinishAsync(session.Id, CancellationToken.None);
        await analyzer.AnalyzeEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var cancellation = coordinator.CancelAsync(session.Id, CancellationToken.None);
        analyzeGate.TrySetResult();

        await Task.WhenAll(finish, cancellation);

        CollectionAssert.AreEqual(CancellationAfterAnalyzeBackendCalls, backend.Calls);
        CollectionAssert.AreEqual(AnalyzeCalls, analyzer.Calls);
        Assert.AreEqual(AnalysisSessionState.Canceled, session.State);
        Assert.IsEmpty(bus.Events.OfType<AnalysisCompleted>());
    }

    [TestMethod]
    public async Task FinishAsync_WhenAlreadyFinishing_ReturnsTheSameInFlightTask()
    {
        var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new ControlledCaptureBackend { StopGate = stopGate };
        await using var bus = new RecordingEventBus();
        var coordinator = CreateCoordinator(backend, new ControlledAnalysisService(), bus);

        var session = await coordinator.StartAsync(CancellationToken.None);
        var first = coordinator.FinishAsync(session.Id, CancellationToken.None);
        await backend.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var repeated = coordinator.FinishAsync(session.Id, CancellationToken.None);
        stopGate.TrySetResult();

        Assert.AreSame(first, repeated);
        await Task.WhenAll(first, repeated);
    }

    [TestMethod]
    public async Task CancelAsync_WhenAlreadyCanceling_ReturnsTheSameInFlightTask()
    {
        var cancelGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new ControlledCaptureBackend { CancelGate = cancelGate };
        await using var bus = new RecordingEventBus();
        var coordinator = CreateCoordinator(backend, new ControlledAnalysisService(), bus);

        var session = await coordinator.StartAsync(CancellationToken.None);
        var first = coordinator.CancelAsync(session.Id, CancellationToken.None);
        await backend.CancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var repeated = coordinator.CancelAsync(session.Id, CancellationToken.None);
        cancelGate.TrySetResult();

        Assert.AreSame(first, repeated);
        await Task.WhenAll(first, repeated);
    }

    [TestMethod]
    public async Task FinishAsync_DuringCanceling_RejectsInsteadOfCoalescingDifferentOperationKinds()
    {
        var cancelGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new ControlledCaptureBackend { CancelGate = cancelGate };
        await using var bus = new RecordingEventBus();
        var coordinator = CreateCoordinator(backend, new ControlledAnalysisService(), bus);

        var session = await coordinator.StartAsync(CancellationToken.None);
        var cancellation = coordinator.CancelAsync(session.Id, CancellationToken.None);
        await backend.CancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Throws<InvalidOperationException>(() => coordinator.FinishAsync(session.Id, CancellationToken.None));

        cancelGate.TrySetResult();
        await cancellation;
    }

    [TestMethod]
    public async Task FinishAsync_WhenCancellationIsPendingForActiveFinish_Rejects()
    {
        var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new ControlledCaptureBackend { StopGate = stopGate };
        await using var bus = new RecordingEventBus();
        var coordinator = CreateCoordinator(backend, new ControlledAnalysisService(), bus);

        var session = await coordinator.StartAsync(CancellationToken.None);
        var finish = coordinator.FinishAsync(session.Id, CancellationToken.None);
        await backend.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var cancellation = coordinator.CancelAsync(session.Id, CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() => coordinator.FinishAsync(session.Id, CancellationToken.None));

        stopGate.TrySetResult();
        await Task.WhenAll(finish, cancellation);
    }

    [TestMethod]
    public async Task FinishAsync_UnknownSession_Rejects()
    {
        await using var bus = new RecordingEventBus();
        var coordinator = CreateCoordinator(new ControlledCaptureBackend(), new ControlledAnalysisService(), bus);

        Assert.Throws<KeyNotFoundException>(() => coordinator.FinishAsync(SessionId.New(), CancellationToken.None));
    }

    [TestMethod]
    public async Task FinishAsync_WhenStoppingTraceFails_PublishesFailureWithoutStartingLaterStages()
    {
        var backend = new ControlledCaptureBackend { StopException = new InvalidOperationException("Stop failed.") };
        var analyzer = new ControlledAnalysisService();
        await using var bus = new RecordingEventBus();
        var coordinator = CreateCoordinator(backend, analyzer, bus);

        var session = await coordinator.StartAsync(CancellationToken.None);
        await coordinator.FinishAsync(session.Id, CancellationToken.None);

        CollectionAssert.AreEqual(StopFailureCalls, backend.Calls);
        Assert.IsEmpty(analyzer.Calls);
        Assert.AreEqual(AnalysisSessionState.Failed, session.State);
        Assert.HasCount(1, bus.Events.OfType<AnalysisFailed>());
    }

    [TestMethod]
    public async Task CancelAsync_WhenBackendFails_TransitionsToFailedAndPublishesFailure()
    {
        var backend = new ControlledCaptureBackend { CancelException = new InvalidOperationException("Cancellation failed.") };
        await using var bus = new RecordingEventBus();
        var coordinator = CreateCoordinator(backend, new ControlledAnalysisService(), bus);

        var session = await coordinator.StartAsync(CancellationToken.None);
        await coordinator.CancelAsync(session.Id, CancellationToken.None);

        CollectionAssert.AreEqual(CancellationCalls, backend.Calls);
        Assert.AreEqual(AnalysisSessionState.Failed, session.State);
        Assert.HasCount(1, bus.Events.OfType<AnalysisFailed>());
    }

    private static AnalysisSessionCoordinator CreateCoordinator(ICaptureBackend backend, IAnalysisService analyzer, IEventBus bus)
    {
        return new AnalysisSessionCoordinator(backend, analyzer, bus, TimeProvider.System);
    }

    private static void AssertBoundary(ControlledCaptureBackend backend, string operation, AnalysisSessionState expectedState)
    {
        Assert.AreEqual(expectedState, backend.Boundaries.Single(boundary => boundary.Operation == operation).State);
    }

    private static void AssertBoundary(ControlledAnalysisService analyzer, string operation, AnalysisSessionState expectedState)
    {
        Assert.AreEqual(expectedState, analyzer.Boundaries.Single(boundary => boundary.Operation == operation).State);
    }

    private sealed class ControlledCaptureBackend : ICaptureBackend
    {
        public List<string> Calls { get; } = [];
        public List<BackendBoundary> Boundaries { get; } = [];
        public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SnapshotEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancelEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? StartGate { get; init; }
        public TaskCompletionSource? StopGate { get; init; }
        public TaskCompletionSource? SnapshotGate { get; init; }
        public TaskCompletionSource? CancelGate { get; init; }
        public Exception? StopException { get; init; }
        public Exception? CancelException { get; init; }
        public AnalysisSession? LastSession { get; private set; }

        public async Task StartAllocationTraceAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            LastSession = session;
            Record("start", session, cancellationToken);
            StartEntered.TrySetResult();
            await WaitForGateAsync(StartGate).ConfigureAwait(false);
        }

        public async Task StopAllocationTraceAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            Record("stop", session, cancellationToken);
            StopEntered.TrySetResult();
            await WaitForGateAsync(StopGate).ConfigureAwait(false);
            if (StopException is not null) throw StopException;
        }

        public async Task CaptureHeapSnapshotAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            Record("snapshot", session, cancellationToken);
            SnapshotEntered.TrySetResult();
            await WaitForGateAsync(SnapshotGate).ConfigureAwait(false);
        }

        public async Task CancelAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            Record("cancel", session, cancellationToken);
            CancelEntered.TrySetResult();
            await WaitForGateAsync(CancelGate).ConfigureAwait(false);
            if (CancelException is not null) throw CancelException;
        }

        private void Record(string operation, AnalysisSession session, CancellationToken cancellationToken)
        {
            Calls.Add(operation);
            Boundaries.Add(new BackendBoundary(operation, session.State, cancellationToken.CanBeCanceled));
        }

        private static async Task WaitForGateAsync(TaskCompletionSource? gate)
        {
            if (gate is not null) await gate.Task.ConfigureAwait(false);
        }
    }

    private sealed class ControlledAnalysisService : IAnalysisService
    {
        public List<string> Calls { get; } = [];
        public List<BackendBoundary> Boundaries { get; } = [];
        public TaskCompletionSource AnalyzeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? AnalyzeGate { get; init; }

        public async Task AnalyzeAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            Calls.Add("analyze");
            Boundaries.Add(new BackendBoundary("analyze", session.State, cancellationToken.CanBeCanceled));
            AnalyzeEntered.TrySetResult();
            if (AnalyzeGate is not null) await AnalyzeGate.Task.ConfigureAwait(false);
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

        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler, EventSubscriptionOptions? options = null)
            where TEvent : IApplicationEvent => NoOpDisposable.Instance;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public static NoOpDisposable Instance { get; } = new();
        public void Dispose() { }
    }

    private sealed record BackendBoundary(string Operation, AnalysisSessionState State, bool TokenCanBeCanceled);
}
