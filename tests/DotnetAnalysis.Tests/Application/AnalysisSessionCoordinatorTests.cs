using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Application.Contracts;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Application.Sessions;
using DotnetAnalysis.Core.Events;
using DotnetAnalysis.Core.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private static readonly string[] StopFailureCalls = ["start", "stop", "cancel"];
    private static readonly string[] SnapshotFailureCalls = ["start", "stop", "snapshot", "cancel"];
    private static readonly string[] AnalyzeCalls = ["analyze"];

    [TestMethod]
    public async Task FinishAsync_StopsTraceBeforeSnapshotBeforeAnalysis()
    {
        var calls = new List<string>();
        var backend = new ControlledCaptureBackend(calls);
        var analyzer = new ControlledAnalysisService(calls);
        await using var bus = new RecordingEventBus();
        var coordinator = CreateCoordinator(backend, analyzer, bus);

        var session = await coordinator.StartAsync(CancellationToken.None);
        await coordinator.FinishAsync(session.Id, CancellationToken.None);

        CollectionAssert.AreEqual(NormalCompletionCalls, calls);
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
    public async Task StartAsync_WhenCaptureStartedPublicationThrowsSynchronously_CancelsTraceBeforeFailingSession()
    {
        var backend = new ControlledCaptureBackend();
        await using var bus = new RecordingEventBus(throwCaptureStartedSynchronously: true);
        var coordinator = CreateCoordinator(backend, new ControlledAnalysisService(), bus);

        var session = await coordinator.StartAsync(CancellationToken.None);

        CollectionAssert.AreEqual(CancellationCalls, backend.Calls);
        Assert.AreEqual(AnalysisSessionState.Failed, session.State);
        AssertBoundary(backend, "cancel", AnalysisSessionState.Canceling);
        Assert.HasCount(1, bus.Events.OfType<AnalysisFailed>());
    }

    [TestMethod]
    public async Task FinishAsync_WhenCompletedPublicationThrowsSynchronously_LogsFailureAndKeepsCompletedState()
    {
        var calls = new List<string>();
        var backend = new ControlledCaptureBackend(calls);
        var analyzer = new ControlledAnalysisService(calls);
        var publicationFailure = new EventDeliveryException(typeof(AnalysisCompleted), "test-subscription");
        await using var bus = new RecordingEventBus(synchronousTerminalPublicationException: publicationFailure);
        var logger = new RecordingLogger<AnalysisSessionCoordinator>();
        var coordinator = CreateCoordinator(backend, analyzer, bus, logger);

        var session = await coordinator.StartAsync(CancellationToken.None);
        await coordinator.FinishAsync(session.Id, CancellationToken.None);

        CollectionAssert.AreEqual(NormalCompletionCalls, calls);
        Assert.AreEqual(AnalysisSessionState.Completed, session.State);
        AssertTerminalPublicationFailure(logger, session.Id, nameof(AnalysisCompleted), publicationFailure);
    }

    [TestMethod]
    public async Task CancelAsync_WhenCanceledPublicationFailsAsynchronously_LogsFailureAndKeepsCanceledState()
    {
        var publicationFailure = new EventDeliveryException(typeof(AnalysisCanceled), "test-subscription");
        var backend = new ControlledCaptureBackend();
        await using var bus = new RecordingEventBus(asynchronousTerminalPublicationException: publicationFailure);
        var logger = new RecordingLogger<AnalysisSessionCoordinator>();
        var coordinator = CreateCoordinator(backend, new ControlledAnalysisService(), bus, logger);

        var session = await coordinator.StartAsync(CancellationToken.None);
        await coordinator.CancelAsync(session.Id, CancellationToken.None);

        CollectionAssert.AreEqual(CancellationCalls, backend.Calls);
        Assert.AreEqual(AnalysisSessionState.Canceled, session.State);
        AssertTerminalPublicationFailure(logger, session.Id, nameof(AnalysisCanceled), publicationFailure);
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
    public async Task CancelAsync_DuringNonTokenResponsiveStart_InvokesBackendCancellationPromptly()
    {
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new ControlledCaptureBackend
        {
            StartGate = startGate,
            OnCancel = () => startGate.TrySetResult()
        };
        await using var bus = new RecordingEventBus();
        var coordinator = CreateCoordinator(backend, new ControlledAnalysisService(), bus);

        var start = coordinator.StartAsync(CancellationToken.None);
        await backend.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var session = backend.LastSession!;
        var cancellation = coordinator.CancelAsync(session.Id, CancellationToken.None);

        try
        {
            await backend.CancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await Task.WhenAll(start, cancellation).WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            startGate.TrySetResult();
            await Task.WhenAll(start, cancellation).WaitAsync(TimeSpan.FromSeconds(1));
        }

        CollectionAssert.AreEqual(CancellationCalls, backend.Calls);
        Assert.AreEqual(AnalysisSessionState.Canceled, session.State);
    }

    [TestMethod]
    public async Task CancelAsync_WhenBackendReleasesActiveStartInline_CancelsWithoutDisposedTokenRace()
    {
        var startGate = new TaskCompletionSource();
        var backend = new ControlledCaptureBackend
        {
            StartGate = startGate,
            OnCancel = () => startGate.TrySetResult()
        };
        await using var bus = new RecordingEventBus();
        var coordinator = CreateCoordinator(backend, new ControlledAnalysisService(), bus);

        var start = coordinator.StartAsync(CancellationToken.None);
        await backend.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var session = backend.LastSession!;

        var cancellation = coordinator.CancelAsync(session.Id, CancellationToken.None);
        await Task.WhenAll(start, cancellation).WaitAsync(TimeSpan.FromSeconds(1));

        CollectionAssert.AreEqual(CancellationCalls, backend.Calls);
        Assert.AreEqual(1, backend.Calls.Count(call => call == "cancel"));
        Assert.IsEmpty(bus.Events.OfType<CaptureStarted>());
        Assert.AreEqual(AnalysisSessionState.Canceled, session.State);
    }

    [TestMethod]
    public async Task CancelAsync_WhenActiveTokenCallbackThrows_StillConvergesAndClearsCancellationOperation()
    {
        var callbackFailure = new InvalidOperationException("Cancellation callback failed.");
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new ControlledCaptureBackend
        {
            StartGate = startGate,
            CancelGate = cancelGate,
            CancellationCallbackException = callbackFailure,
            OnCancel = () => startGate.TrySetResult()
        };
        await using var bus = new RecordingEventBus();
        var logger = new RecordingLogger<AnalysisSessionCoordinator>();
        var coordinator = CreateCoordinator(backend, new ControlledAnalysisService(), bus, logger);

        var start = coordinator.StartAsync(CancellationToken.None);
        await backend.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var session = backend.LastSession!;
        Task? cancellation = null;

        try
        {
            cancellation = coordinator.CancelAsync(session.Id, CancellationToken.None);
            await backend.CancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var repeatedCancellation = coordinator.CancelAsync(session.Id, CancellationToken.None);

            Assert.AreSame(cancellation, repeatedCancellation);
            Assert.AreEqual(1, backend.Calls.Count(call => call == "cancel"));
            Assert.AreEqual(AnalysisSessionState.Canceling, session.State);
            Assert.IsEmpty(bus.Events.OfType<CaptureStarted>());

            cancelGate.TrySetResult();
            await Task.WhenAll(start, cancellation, repeatedCancellation).WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            startGate.TrySetResult();
            cancelGate.TrySetResult();
            await start.WaitAsync(TimeSpan.FromSeconds(1));
            if (cancellation is not null)
            {
                await cancellation.WaitAsync(TimeSpan.FromSeconds(1));
            }
        }

        Assert.AreEqual(AnalysisSessionState.Failed, session.State);
        Assert.AreEqual(1, backend.Calls.Count(call => call == "cancel"));
        Assert.HasCount(1, bus.Events.OfType<AnalysisFailed>());
        Assert.IsEmpty(bus.Events.OfType<CaptureStarted>());

        var signalFailure = logger.Entries.Single(log => log.EventId.Name == "SessionStageFailed");
        Assert.AreEqual(LogLevel.Error, signalFailure.LogLevel);
        Assert.AreEqual(session.Id, signalFailure.Properties["SessionId"]);
        Assert.AreEqual("SignalCancellation", signalFailure.Properties["Stage"]);
        var aggregateFailure = signalFailure.Exception as AggregateException;
        Assert.IsNotNull(aggregateFailure);
        Assert.AreSame(callbackFailure, aggregateFailure.InnerExceptions.Single());

        Assert.Throws<InvalidOperationException>(() => coordinator.CancelAsync(session.Id, CancellationToken.None));
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
    public async Task StartAsync_WhenStartingTraceFails_CleansUpBeforePublishingFailure()
    {
        var cancelGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stageFailure = new InvalidOperationException("Start failed.");
        var backend = new ControlledCaptureBackend
        {
            StartException = stageFailure,
            CancelGate = cancelGate
        };
        await using var bus = new RecordingEventBus();
        var logger = new RecordingLogger<AnalysisSessionCoordinator>();
        var coordinator = CreateCoordinator(backend, new ControlledAnalysisService(), bus, logger);

        var start = coordinator.StartAsync(CancellationToken.None);

        try
        {
            await backend.CancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(AnalysisSessionState.Canceling, backend.LastSession!.State);
            Assert.IsEmpty(bus.Events.OfType<AnalysisFailed>());
        }
        finally
        {
            cancelGate.TrySetResult();
        }

        var session = await start.WaitAsync(TimeSpan.FromSeconds(1));
        CollectionAssert.AreEqual(CancellationCalls, backend.Calls);
        Assert.AreEqual(AnalysisSessionState.Failed, session.State);
        AssertBoundary(backend, "cancel", AnalysisSessionState.Canceling);
        Assert.HasCount(1, bus.Events.OfType<AnalysisFailed>());
        AssertStageFailure(logger, session.Id, "StartAllocationTrace", stageFailure);
    }

    [TestMethod]
    public async Task FinishAsync_WhenStoppingTraceFails_CleansUpBeforePublishingFailure()
    {
        var cancelGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stageFailure = new InvalidOperationException("Stop failed.");
        var backend = new ControlledCaptureBackend
        {
            StopException = stageFailure,
            CancelGate = cancelGate
        };
        var analyzer = new ControlledAnalysisService();
        await using var bus = new RecordingEventBus();
        var logger = new RecordingLogger<AnalysisSessionCoordinator>();
        var coordinator = CreateCoordinator(backend, analyzer, bus, logger);

        var session = await coordinator.StartAsync(CancellationToken.None);
        var finish = coordinator.FinishAsync(session.Id, CancellationToken.None);

        try
        {
            await backend.CancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(AnalysisSessionState.Canceling, session.State);
            Assert.IsEmpty(bus.Events.OfType<AnalysisFailed>());
        }
        finally
        {
            cancelGate.TrySetResult();
        }

        await finish.WaitAsync(TimeSpan.FromSeconds(1));

        CollectionAssert.AreEqual(StopFailureCalls, backend.Calls);
        Assert.IsEmpty(analyzer.Calls);
        Assert.AreEqual(AnalysisSessionState.Failed, session.State);
        AssertBoundary(backend, "cancel", AnalysisSessionState.Canceling);
        Assert.HasCount(1, bus.Events.OfType<AnalysisFailed>());
        AssertStageFailure(logger, session.Id, "StopAllocationTrace", stageFailure);
    }

    [TestMethod]
    public async Task FinishAsync_WhenSnapshotFails_CleansUpBeforePublishingFailure()
    {
        var cancelGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stageFailure = new InvalidOperationException("Snapshot failed.");
        var backend = new ControlledCaptureBackend
        {
            SnapshotException = stageFailure,
            CancelGate = cancelGate
        };
        var analyzer = new ControlledAnalysisService();
        await using var bus = new RecordingEventBus();
        var logger = new RecordingLogger<AnalysisSessionCoordinator>();
        var coordinator = CreateCoordinator(backend, analyzer, bus, logger);

        var session = await coordinator.StartAsync(CancellationToken.None);
        var finish = coordinator.FinishAsync(session.Id, CancellationToken.None);

        try
        {
            await backend.CancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(AnalysisSessionState.Canceling, session.State);
            Assert.IsEmpty(bus.Events.OfType<AnalysisFailed>());
        }
        finally
        {
            cancelGate.TrySetResult();
        }

        await finish.WaitAsync(TimeSpan.FromSeconds(1));

        CollectionAssert.AreEqual(SnapshotFailureCalls, backend.Calls);
        Assert.IsEmpty(analyzer.Calls);
        Assert.AreEqual(AnalysisSessionState.Failed, session.State);
        AssertBoundary(backend, "cancel", AnalysisSessionState.Canceling);
        Assert.HasCount(1, bus.Events.OfType<AnalysisFailed>());
        AssertStageFailure(logger, session.Id, "CaptureHeapSnapshot", stageFailure);
    }

    [TestMethod]
    public async Task FinishAsync_WhenAnalysisFails_CleansUpBeforePublishingFailure()
    {
        var cancelGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new ControlledCaptureBackend { CancelGate = cancelGate };
        var stageFailure = new InvalidOperationException("Analysis failed.");
        var analyzer = new ControlledAnalysisService
        {
            AnalyzeException = stageFailure
        };
        await using var bus = new RecordingEventBus();
        var logger = new RecordingLogger<AnalysisSessionCoordinator>();
        var coordinator = CreateCoordinator(backend, analyzer, bus, logger);

        var session = await coordinator.StartAsync(CancellationToken.None);
        var finish = coordinator.FinishAsync(session.Id, CancellationToken.None);

        try
        {
            await backend.CancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(AnalysisSessionState.Canceling, session.State);
            Assert.IsEmpty(bus.Events.OfType<AnalysisFailed>());
        }
        finally
        {
            cancelGate.TrySetResult();
        }

        await finish.WaitAsync(TimeSpan.FromSeconds(1));

        CollectionAssert.AreEqual(SnapshotFailureCalls, backend.Calls);
        CollectionAssert.AreEqual(AnalyzeCalls, analyzer.Calls);
        Assert.AreEqual(AnalysisSessionState.Failed, session.State);
        AssertBoundary(backend, "cancel", AnalysisSessionState.Canceling);
        Assert.HasCount(1, bus.Events.OfType<AnalysisFailed>());
        AssertStageFailure(logger, session.Id, "Analyze", stageFailure);
    }

    [TestMethod]
    public async Task CancelAsync_WhenBackendFails_TransitionsToFailedAndPublishesFailure()
    {
        var stageFailure = new InvalidOperationException("Cancellation failed.");
        var backend = new ControlledCaptureBackend { CancelException = stageFailure };
        await using var bus = new RecordingEventBus();
        var logger = new RecordingLogger<AnalysisSessionCoordinator>();
        var coordinator = CreateCoordinator(backend, new ControlledAnalysisService(), bus, logger);

        var session = await coordinator.StartAsync(CancellationToken.None);
        await coordinator.CancelAsync(session.Id, CancellationToken.None);

        CollectionAssert.AreEqual(CancellationCalls, backend.Calls);
        Assert.AreEqual(AnalysisSessionState.Failed, session.State);
        Assert.HasCount(1, bus.Events.OfType<AnalysisFailed>());
        AssertStageFailure(logger, session.Id, "Cancel", stageFailure);
    }

    private static AnalysisSessionCoordinator CreateCoordinator(
        ICaptureBackend backend,
        IAnalysisService analyzer,
        IEventBus bus,
        ILogger<AnalysisSessionCoordinator>? logger = null)
    {
        return new AnalysisSessionCoordinator(
            backend,
            analyzer,
            bus,
            TimeProvider.System,
            logger ?? NullLogger<AnalysisSessionCoordinator>.Instance);
    }

    private static void AssertBoundary(ControlledCaptureBackend backend, string operation, AnalysisSessionState expectedState)
    {
        Assert.AreEqual(expectedState, backend.Boundaries.Single(boundary => boundary.Operation == operation).State);
    }

    private static void AssertBoundary(ControlledAnalysisService analyzer, string operation, AnalysisSessionState expectedState)
    {
        Assert.AreEqual(expectedState, analyzer.Boundaries.Single(boundary => boundary.Operation == operation).State);
    }

    private static void AssertStageFailure(
        RecordingLogger<AnalysisSessionCoordinator> logger,
        SessionId sessionId,
        string stage,
        Exception expectedException)
    {
        var entry = logger.Entries.Single(log => log.EventId.Name == "SessionStageFailed");
        Assert.AreEqual(LogLevel.Error, entry.LogLevel);
        Assert.AreSame(expectedException, entry.Exception);
        Assert.AreEqual(sessionId, entry.Properties["SessionId"]);
        Assert.AreEqual(stage, entry.Properties["Stage"]);
    }

    private static void AssertTerminalPublicationFailure(
        RecordingLogger<AnalysisSessionCoordinator> logger,
        SessionId sessionId,
        string eventType,
        Exception expectedException)
    {
        var entry = logger.Entries.Single(log => log.EventId.Name == "TerminalEventPublicationFailed");
        Assert.AreEqual(LogLevel.Error, entry.LogLevel);
        Assert.AreSame(expectedException, entry.Exception);
        Assert.AreEqual(sessionId, entry.Properties["SessionId"]);
        Assert.AreEqual(eventType, entry.Properties["EventType"]);
    }

    private sealed class ControlledCaptureBackend(List<string>? orderedCalls = null) : ICaptureBackend
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
        public Exception? StartException { get; init; }
        public Exception? StopException { get; init; }
        public Exception? SnapshotException { get; init; }
        public Exception? CancelException { get; init; }
        public Exception? CancellationCallbackException { get; init; }
        public Action? OnCancel { get; init; }
        public AnalysisSession? LastSession { get; private set; }

        public async Task StartAllocationTraceAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            LastSession = session;
            Record("start", session, cancellationToken);
            var callbackException = CancellationCallbackException;
            using var registration = callbackException is null
                ? default
                : cancellationToken.Register(() => throw callbackException);
            StartEntered.TrySetResult();
            await WaitForGateAsync(StartGate).ConfigureAwait(false);
            if (StartException is not null) throw StartException;
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
            if (SnapshotException is not null) throw SnapshotException;
        }

        public async Task CancelAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            Record("cancel", session, cancellationToken);
            CancelEntered.TrySetResult();
            OnCancel?.Invoke();
            await WaitForGateAsync(CancelGate).ConfigureAwait(false);
            if (CancelException is not null) throw CancelException;
        }

        private void Record(string operation, AnalysisSession session, CancellationToken cancellationToken)
        {
            Calls.Add(operation);
            orderedCalls?.Add(operation);
            Boundaries.Add(new BackendBoundary(operation, session.State, cancellationToken.CanBeCanceled));
        }

        private static async Task WaitForGateAsync(TaskCompletionSource? gate)
        {
            if (gate is not null) await gate.Task.ConfigureAwait(false);
        }
    }

    private sealed class ControlledAnalysisService(List<string>? orderedCalls = null) : IAnalysisService
    {
        public List<string> Calls { get; } = [];
        public List<BackendBoundary> Boundaries { get; } = [];
        public TaskCompletionSource AnalyzeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? AnalyzeGate { get; init; }
        public Exception? AnalyzeException { get; init; }

        public async Task AnalyzeAsync(AnalysisSession session, CancellationToken cancellationToken)
        {
            Calls.Add("analyze");
            orderedCalls?.Add("analyze");
            Boundaries.Add(new BackendBoundary("analyze", session.State, cancellationToken.CanBeCanceled));
            AnalyzeEntered.TrySetResult();
            if (AnalyzeGate is not null) await AnalyzeGate.Task.ConfigureAwait(false);
            if (AnalyzeException is not null) throw AnalyzeException;
        }
    }

    private sealed class RecordingEventBus(
        bool failCaptureStarted = false,
        bool throwCaptureStartedSynchronously = false,
        Exception? synchronousTerminalPublicationException = null,
        Exception? asynchronousTerminalPublicationException = null) : IEventBus
    {
        public List<IApplicationEvent> Events { get; } = [];

        public ValueTask PublishAsync<TEvent>(TEvent applicationEvent, CancellationToken cancellationToken)
            where TEvent : IApplicationEvent
        {
            Events.Add(applicationEvent);
            if (throwCaptureStartedSynchronously && applicationEvent is CaptureStarted)
            {
                throw new EventDeliveryException(typeof(CaptureStarted), "test-subscription");
            }

            if (synchronousTerminalPublicationException is not null
                && applicationEvent is AnalysisCompleted or AnalysisCanceled or AnalysisFailed)
            {
                throw synchronousTerminalPublicationException;
            }

            if (failCaptureStarted && applicationEvent is CaptureStarted)
            {
                return ValueTask.FromException(new EventDeliveryException(typeof(CaptureStarted), "test-subscription"));
            }

            return asynchronousTerminalPublicationException is not null
                && applicationEvent is AnalysisCompleted or AnalysisCanceled or AnalysisFailed
                    ? ValueTask.FromException(asynchronousTerminalPublicationException)
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

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly object _syncRoot = new();
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_syncRoot)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NoOpDisposable.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, object?>();

            lock (_syncRoot)
            {
                _entries.Add(new LogEntry(logLevel, eventId, exception, properties));
            }
        }
    }

    private sealed record BackendBoundary(string Operation, AnalysisSessionState State, bool TokenCanBeCanceled);

    private sealed record LogEntry(
        LogLevel LogLevel,
        EventId EventId,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties);
}
