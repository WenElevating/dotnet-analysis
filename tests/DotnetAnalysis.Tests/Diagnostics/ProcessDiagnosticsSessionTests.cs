using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;
using DotnetAnalysis.Diagnostics.Windows;
using DotnetAnalysis.Diagnostics.Windows.Capture;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetAnalysis.Tests.Diagnostics;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class ProcessDiagnosticsSessionTests
{
    private readonly TargetProcess _process = new(4567, Utc("2026-09-02T08:00:00Z"), "target", null);

    [TestMethod]
    public async Task CaptureSnapshotAsync_RejectsConcurrentCapture()
    {
        var capture = new ControlledCapture { CaptureGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var session = CreateSession(capture);

        var firstCapture = session.CaptureSnapshotAsync(CancellationToken.None);
        await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await session.CaptureSnapshotAsync(CancellationToken.None));

        capture.CaptureGate.TrySetResult();
        var snapshot = await firstCapture.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(MemorySnapshotState.Analyzing, snapshot.State);
        Assert.AreEqual(ProcessDiagnosticsSessionState.Monitoring, session.State);
        Assert.AreEqual(1, capture.Calls);
    }

    [TestMethod]
    public async Task CaptureSnapshotAsync_WhenCaptureIsCancelled_ClearsCaptureGateForRetry()
    {
        var capture = new ControlledCapture
        {
            Failure = new DiagnosticsException(DiagnosticsErrorCode.CaptureCancelled, "Capture cancelled.")
        };
        await using var session = CreateSession(capture);

        var exception = await Assert.ThrowsAsync<DiagnosticsException>(
            async () => await session.CaptureSnapshotAsync(CancellationToken.None));

        Assert.AreEqual(DiagnosticsErrorCode.CaptureCancelled, exception.ErrorCode);
        Assert.AreEqual(ProcessDiagnosticsSessionState.Monitoring, session.State);

        capture.Failure = null;
        var snapshot = await session.CaptureSnapshotAsync(CancellationToken.None);

        Assert.AreEqual(MemorySnapshotState.Analyzing, snapshot.State);
        Assert.AreEqual(2, capture.Calls);
    }

    [TestMethod]
    public async Task EndAsync_CancelsInFlightCaptureAndDisposesAllocationCollector()
    {
        var capture = new ControlledCapture { WaitForCancellation = true };
        var allocationCollector = new AllocationSamplingSession(
            new AllocationProfileBuilder(Utc("2026-09-02T08:00:00Z")));
        await using var session = CreateSession(capture, allocationCollector: allocationCollector);

        var activeCapture = session.CaptureSnapshotAsync(CancellationToken.None);
        await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await session.EndAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        await session.DisposeAsync();

        var exception = await Assert.ThrowsAsync<DiagnosticsException>(
            async () => await activeCapture.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.AreEqual(DiagnosticsErrorCode.CaptureCancelled, exception.ErrorCode);
        Assert.AreEqual(ProcessDiagnosticsSessionState.Ended, session.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await allocationCollector.StartAsync(_process, CancellationToken.None));
    }

    [TestMethod]
    public async Task GetExecutionProfileAsync_WhenExecutionSamplingIsUnavailable_KeepsOtherSessionOperationsAvailable()
    {
        var capture = new ControlledCapture();
        var executionSampling = new ControlledExecutionSamplingSession { IsUnavailable = true };
        await using var session = CreateSession(capture, executionSampling: executionSampling);

        await using var samples = session.GetMemoryUsageAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.IsTrue(await samples.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(MemoryUsageSampleState.Unavailable, samples.Current.State);

        var snapshot = await session.CaptureSnapshotAsync(CancellationToken.None);
        var exception = await Assert.ThrowsAsync<DiagnosticsException>(
            async () => await session.GetExecutionProfileAsync(TimeRange(), CancellationToken.None));

        Assert.AreEqual(DiagnosticsErrorCode.ExecutionProfilingUnavailable, exception.ErrorCode);
        Assert.AreEqual(MemorySnapshotState.Analyzing, snapshot.State);
        Assert.AreEqual(ProcessDiagnosticsSessionState.Monitoring, session.State);
    }

    [TestMethod]
    public async Task GetExecutionProfileAsync_AllowsSnapshotCaptureToRunConcurrently()
    {
        var capture = new ControlledCapture
        {
            CaptureGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var executionSampling = new ControlledExecutionSamplingSession
        {
            QueryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var session = CreateSession(capture, executionSampling: executionSampling);

        var profileTask = session.GetExecutionProfileAsync(TimeRange(), CancellationToken.None);
        var snapshotTask = session.CaptureSnapshotAsync(CancellationToken.None);
        await Task.WhenAll(
            executionSampling.QueryStarted.Task,
            capture.Started.Task).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsFalse(profileTask.IsCompleted);
        Assert.IsFalse(snapshotTask.IsCompleted);

        executionSampling.QueryGate.TrySetResult();
        capture.CaptureGate.TrySetResult();
        var profile = await profileTask.WaitAsync(TimeSpan.FromSeconds(1));
        var snapshot = await snapshotTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(TimeRange(), profile.TimeRange);
        Assert.AreEqual(MemorySnapshotState.Analyzing, snapshot.State);
    }

    [TestMethod]
    public async Task EndAsync_CancelsExecutionQueryAndDisposesExecutionSamplingBeforeMemorySampling()
    {
        var disposalOrder = new List<string>();
        var capture = new ControlledCapture { WaitForCancellation = true };
        var executionSampling = new ControlledExecutionSamplingSession
        {
            WaitForQueryCancellation = true,
            DisposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            DisposalOrder = disposalOrder
        };
        var managedHeapReader = new RecordingManagedHeapReader(disposalOrder);
        var sampler = new ProcessMemorySampler(
            _process,
            new UnavailableProcessMemoryReader(),
            managedHeapReader,
            TimeProvider.System,
            TimeSpan.Zero);
        var allocationCollector = new AllocationSamplingSession(
            new AllocationProfileBuilder(Utc("2026-09-02T08:00:00Z")));
        await using var session = new ProcessDiagnosticsSession(
            _process,
            sampler,
            allocationCollector,
            executionSampling,
            capture,
            new RecordingEventBus(),
            TimeProvider.System,
            NullLogger<ProcessDiagnosticsSession>.Instance,
            static _ => true);

        var profileTask = session.GetExecutionProfileAsync(TimeRange(), CancellationToken.None);
        var snapshotTask = session.CaptureSnapshotAsync(CancellationToken.None);
        await Task.WhenAll(
            executionSampling.QueryStarted.Task,
            capture.Started.Task).WaitAsync(TimeSpan.FromSeconds(1));

        var endTask = session.EndAsync(CancellationToken.None);
        await executionSampling.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(ProcessDiagnosticsSessionState.Ending, session.State);
        Assert.IsTrue(snapshotTask.IsCompleted);
        Assert.IsTrue(executionSampling.QueryCancellationObserved.Task.IsCompletedSuccessfully);
        Assert.HasCount(1, disposalOrder);
        Assert.AreEqual("execution", disposalOrder[0]);

        executionSampling.DisposeGate.TrySetResult();
        await endTask.WaitAsync(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await profileTask.WaitAsync(TimeSpan.FromSeconds(1)));
        var captureException = await Assert.ThrowsAsync<DiagnosticsException>(
            async () => await snapshotTask.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(DiagnosticsErrorCode.CaptureCancelled, captureException.ErrorCode);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await allocationCollector.StartAsync(_process, CancellationToken.None));
        Assert.HasCount(2, disposalOrder);
        Assert.AreEqual("execution", disposalOrder[0]);
        Assert.AreEqual("memory", disposalOrder[1]);
        Assert.AreEqual(ProcessDiagnosticsSessionState.Ended, session.State);

        var endedQueryException = await Assert.ThrowsAsync<DiagnosticsException>(
            async () => await session.GetExecutionProfileAsync(TimeRange(), CancellationToken.None));
        Assert.AreEqual(DiagnosticsErrorCode.ExecutionProfileRangeUnavailable, endedQueryException.ErrorCode);
    }

    [TestMethod]
    public async Task EndAsync_WhenExecutionCleanupFails_CompletesRemainingCleanupAndTerminalEventBeforeRethrowing()
    {
        var disposalOrder = new List<string>();
        var executionFailure = new IOException("Execution cleanup failed.");
        var executionSampling = new ControlledExecutionSamplingSession
        {
            DisposalOrder = disposalOrder,
            DisposeFailure = executionFailure
        };
        var allocationSampling = new ControlledAllocationSamplingSessionResource(disposalOrder)
        {
            DisposeFailure = new InvalidOperationException("Allocation cleanup failed.")
        };
        var managedHeapReader = new RecordingManagedHeapReader(disposalOrder);
        var sampler = new ProcessMemorySampler(
            _process,
            new UnavailableProcessMemoryReader(),
            managedHeapReader,
            TimeProvider.System,
            TimeSpan.Zero);
        var eventBus = new ControlledTerminalEventBus();
        var session = new ProcessDiagnosticsSession(
            _process,
            sampler,
            allocationSampling,
            executionSampling,
            new ControlledCapture(),
            eventBus,
            TimeProvider.System,
            NullLogger<ProcessDiagnosticsSession>.Instance,
            static _ => true);

        var endTask = session.EndAsync(CancellationToken.None);
        try
        {
            await eventBus.TerminalPublishStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.AreEqual(ProcessDiagnosticsSessionState.Ended, session.State);
            Assert.IsFalse(endTask.IsCompleted);
            AssertDisposalOrder(disposalOrder, "execution", "allocation", "memory");
            Assert.IsTrue(executionSampling.IsDisposed);
            Assert.IsTrue(allocationSampling.IsDisposed);
            Assert.IsTrue(managedHeapReader.IsDisposed);
        }
        finally
        {
            eventBus.TerminalPublishGate.TrySetResult();
        }

        var exception = await CaptureExceptionAsync(
            async () => await endTask.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreSame(executionFailure, exception);
        Assert.IsTrue(eventBus.TerminalPublishCompleted.Task.IsCompletedSuccessfully);
        Assert.AreEqual(1, eventBus.Events.OfType<ProcessDiagnosticsSessionEnded>().Count());

        var disposeException = await CaptureExceptionAsync(
            async () => await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreSame(executionFailure, disposeException);
    }

    [TestMethod]
    public async Task GetMemoryUsageAsync_WhenSessionEndedSampleArrives_CancelsQueriesAndCleansResources()
    {
        var disposalOrder = new List<string>();
        var endedProcess = new TargetProcess(int.MaxValue, Utc("2026-09-02T08:00:00Z"), "ended-target", null);
        var managedHeapReader = new RecordingManagedHeapReader(disposalOrder);
        var sampler = new ProcessMemorySampler(
            endedProcess,
            new ProcessMemoryReader(),
            managedHeapReader,
            TimeProvider.System,
            TimeSpan.Zero);
        var allocationSampling = new ControlledAllocationSamplingSessionResource(disposalOrder);
        var executionSampling = new ControlledExecutionSamplingSession
        {
            WaitForQueryCancellation = true,
            DisposalOrder = disposalOrder
        };
        var eventBus = new RecordingEventBus();
        var session = new ProcessDiagnosticsSession(
            endedProcess,
            sampler,
            allocationSampling,
            executionSampling,
            new ControlledCapture(),
            eventBus,
            TimeProvider.System,
            NullLogger<ProcessDiagnosticsSession>.Instance,
            static _ => true);

        var activeQuery = session.GetExecutionProfileAsync(TimeRange(), CancellationToken.None);
        await executionSampling.QueryStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await foreach (var _ in session.GetMemoryUsageAsync(CancellationToken.None))
        {
        }

        Assert.AreEqual(ProcessDiagnosticsSessionState.Ended, session.State);
        Assert.IsTrue(executionSampling.QueryCancellationObserved.Task.IsCompletedSuccessfully);
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await activeQuery.WaitAsync(TimeSpan.FromSeconds(1)));
        AssertDisposalOrder(disposalOrder, "execution", "allocation", "memory");
        Assert.AreEqual(1, eventBus.Events.OfType<ProcessDiagnosticsSessionEnded>().Count());

        var exception = await Assert.ThrowsAsync<DiagnosticsException>(
            async () => await session.GetExecutionProfileAsync(TimeRange(), CancellationToken.None));
        Assert.AreEqual(DiagnosticsErrorCode.ExecutionProfileRangeUnavailable, exception.ErrorCode);
        Assert.AreEqual(1, executionSampling.QueryCallCount);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task GetExecutionProfileAsync_WhenTargetExitedWithoutTimelineEnumeration_EndsSessionAndCancelsActiveQuery()
    {
        var disposalOrder = new List<string>();
        var targetIsAlive = true;
        var managedHeapReader = new RecordingManagedHeapReader(disposalOrder);
        var sampler = new ProcessMemorySampler(
            _process,
            new UnavailableProcessMemoryReader(),
            managedHeapReader,
            TimeProvider.System,
            TimeSpan.Zero);
        var allocationSampling = new ControlledAllocationSamplingSessionResource(disposalOrder);
        var executionSampling = new ControlledExecutionSamplingSession
        {
            WaitForQueryCancellation = true,
            DisposalOrder = disposalOrder
        };
        var eventBus = new RecordingEventBus();
        var session = new ProcessDiagnosticsSession(
            _process,
            sampler,
            allocationSampling,
            executionSampling,
            new ControlledCapture(),
            eventBus,
            TimeProvider.System,
            NullLogger<ProcessDiagnosticsSession>.Instance,
            _ => targetIsAlive);

        var activeQuery = session.GetExecutionProfileAsync(TimeRange(), CancellationToken.None);
        await executionSampling.QueryStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        targetIsAlive = false;

        try
        {
            var exception = await Assert.ThrowsAsync<DiagnosticsException>(
                async () => await session
                    .GetExecutionProfileAsync(TimeRange(), CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(1)));

            Assert.AreEqual(DiagnosticsErrorCode.ExecutionProfileRangeUnavailable, exception.ErrorCode);
            Assert.AreEqual(ProcessDiagnosticsSessionState.Ended, session.State);
            Assert.IsTrue(executionSampling.QueryCancellationObserved.Task.IsCompletedSuccessfully);
            await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await activeQuery.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.AreEqual(1, executionSampling.QueryCallCount);
            AssertDisposalOrder(disposalOrder, "execution", "allocation", "memory");
            Assert.AreEqual(1, eventBus.Events.OfType<ProcessDiagnosticsSessionEnded>().Count());
        }
        finally
        {
            await session.EndAsync(CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await session.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task EndAsync_PublishesSessionLifecycleEvents()
    {
        var eventBus = new RecordingEventBus();
        await using var session = CreateSession(new ControlledCapture(), eventBus);

        await session.EndAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

        var stateTransitions = eventBus.Events
            .OfType<ProcessDiagnosticsSessionStateChanged>()
            .Select(@event => @event.State)
            .ToArray();

        CollectionAssert.AreEqual(
            new[]
            {
                ProcessDiagnosticsSessionState.Monitoring,
                ProcessDiagnosticsSessionState.Ending,
                ProcessDiagnosticsSessionState.Ended
            },
            stateTransitions);
        Assert.AreEqual(1, eventBus.Events.OfType<ProcessDiagnosticsSessionEnded>().Count());
    }

    [TestMethod]
    public async Task CaptureSnapshotAsync_WhenUnexpectedExceptionOccurs_LogsStructuredStageAndWrapsStableErrorCode()
    {
        var innerException = new InvalidOperationException("boom");
        var logger = new RecordingLogger<ProcessDiagnosticsSession>();
        await using var session = CreateSession(new ControlledCapture { Failure = innerException }, logger: logger);

        var exception = await Assert.ThrowsAsync<DiagnosticsException>(
            async () => await session.CaptureSnapshotAsync(CancellationToken.None));

        Assert.AreEqual(DiagnosticsErrorCode.CaptureFailed, exception.ErrorCode);
        Assert.AreSame(innerException, exception.InnerException);

        var log = logger.Entries.Single(entry => entry.EventId.Name == "DiagnosticsSessionStageFailed");
        Assert.AreEqual(session.Id, log.Properties["SessionId"]);
        Assert.AreEqual("CaptureSnapshot", log.Properties["Stage"]);
        Assert.AreSame(innerException, log.Exception);
    }

    [TestMethod]
    public void DiagnosticsException_PreservesEveryStableErrorCodeAndInnerException()
    {
        var inner = new InvalidOperationException("raw");

        var executionProfilingErrorCodes = new[]
        {
            DiagnosticsErrorCode.ExecutionProfilingUnavailable,
            DiagnosticsErrorCode.ExecutionProfileRangeUnavailable,
            DiagnosticsErrorCode.ExecutionProfileStorageFailed
        };

        foreach (var errorCode in Enum.GetValues<DiagnosticsErrorCode>())
        {
            var exception = new DiagnosticsException(errorCode, "stable", inner);

            Assert.AreEqual(errorCode, exception.ErrorCode);
            Assert.AreEqual("stable", exception.Message);
            Assert.AreSame(inner, exception.InnerException);
        }

        foreach (var errorCode in executionProfilingErrorCodes)
        {
            var exception = new DiagnosticsException(errorCode, "stable", inner);

            Assert.AreEqual(errorCode, exception.ErrorCode);
            Assert.AreEqual("stable", exception.Message);
            Assert.AreSame(inner, exception.InnerException);
        }
    }

    [TestMethod]
    public async Task GetExecutionProfileAsync_WhenSessionDoesNotProvideProfiling_ThrowsStableUnavailableError()
    {
        IProcessDiagnosticsSession session = new InterfaceDefaultExecutionProfileSession(_process);
        var timeRange = new ExecutionTimeRange(
            Utc("2026-09-02T08:00:00Z"),
            Utc("2026-09-02T08:01:00Z"));

        var exception = await Assert.ThrowsAsync<DiagnosticsException>(
            async () => await session.GetExecutionProfileAsync(timeRange, CancellationToken.None));

        Assert.AreEqual(DiagnosticsErrorCode.ExecutionProfilingUnavailable, exception.ErrorCode);
        StringAssert.Contains(exception.Message, "执行采样");
    }

    private ProcessDiagnosticsSession CreateSession(
        ControlledCapture capture,
        IEventBus? eventBus = null,
        ILogger<ProcessDiagnosticsSession>? logger = null,
        AllocationSamplingSession? allocationCollector = null,
        IExecutionSamplingSession? executionSampling = null)
    {
        var sampler = new ProcessMemorySampler(
            _process,
            new UnavailableProcessMemoryReader(),
            new UnavailableManagedHeapReader(),
            TimeProvider.System,
            TimeSpan.Zero);
        allocationCollector ??= new AllocationSamplingSession(
            new AllocationProfileBuilder(Utc("2026-09-02T08:00:00Z")));
        executionSampling ??= new ControlledExecutionSamplingSession();
        return new ProcessDiagnosticsSession(
            _process,
            sampler,
            allocationCollector,
            executionSampling,
            capture,
            eventBus ?? new RecordingEventBus(),
            TimeProvider.System,
            logger ?? NullLogger<ProcessDiagnosticsSession>.Instance,
            static _ => true);
    }

    private static MemorySnapshot Snapshot() =>
        new(
            MemorySnapshotId.New(),
            MemorySnapshotOrigin.Captured,
            Utc("2026-09-02T08:00:05Z"),
            Utc("2026-09-02T08:00:06Z"),
            Utc("2026-09-02T08:00:07Z"),
            MemorySnapshotState.Analyzing);

    private static DateTimeOffset Utc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static ExecutionTimeRange TimeRange() =>
        new(
            Utc("2026-09-02T08:00:00Z"),
            Utc("2026-09-02T08:01:00Z"));

    private static async Task<Exception> CaptureExceptionAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return exception;
        }

        Assert.Fail("Expected the operation to fail.");
        throw new InvalidOperationException("Assert.Fail should have interrupted the test.");
    }

    private static void AssertDisposalOrder(List<string> actual, params string[] expected)
    {
        Assert.HasCount(expected.Length, actual);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.AreEqual(expected[index], actual[index]);
        }
    }

    private sealed class InterfaceDefaultExecutionProfileSession : IProcessDiagnosticsSession
    {
        public InterfaceDefaultExecutionProfileSession(TargetProcess process)
        {
            Process = process;
        }

        public ProcessDiagnosticsSessionId Id { get; } = ProcessDiagnosticsSessionId.New();

        public TargetProcess Process { get; }

        public ProcessDiagnosticsSessionState State => ProcessDiagnosticsSessionState.Monitoring;

        public Task EndAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<MemorySnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromException<MemorySnapshot>(new NotSupportedException());

        public IAsyncEnumerable<MemoryUsageSample> GetMemoryUsageAsync(CancellationToken cancellationToken) =>
            EmptyMemoryUsageSamples();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<MemoryUsageSample> EmptyMemoryUsageSamples()
        {
            yield break;
        }
    }

    private sealed class ControlledCapture : IMemorySnapshotCapture
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? CaptureGate { get; init; }

        public Exception? Failure { get; set; }

        public int Calls { get; private set; }

        public bool WaitForCancellation { get; init; }

        public async Task<MemorySnapshot> CaptureAsync(
            TargetProcess target,
            AllocationSamplingSession allocationCollector,
            CancellationToken cancellationToken)
        {
            Calls++;
            Started.TrySetResult();

            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            if (CaptureGate is not null)
            {
                await CaptureGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            return Snapshot();
        }
    }

    private sealed class UnavailableProcessMemoryReader : IProcessMemoryReader
    {
        public long? ReadPrivateWorkingSetBytes(int processId) => null;
    }

    private sealed class RecordingManagedHeapReader : IManagedHeapReader, IDisposable
    {
        private readonly List<string> _disposalOrder;

        public RecordingManagedHeapReader(List<string> disposalOrder)
        {
            _disposalOrder = disposalOrder;
        }

        public Exception? DisposeFailure { get; init; }

        public bool IsDisposed { get; private set; }

        public long? ReadManagedHeapBytes(int processId) => null;

        public void Dispose()
        {
            _disposalOrder.Add("memory");
            IsDisposed = true;
            if (DisposeFailure is not null)
            {
                throw DisposeFailure;
            }
        }
    }

    private sealed class ControlledExecutionSamplingSession : IExecutionSamplingSession
    {
        private int _queryCallCount;

        public TaskCompletionSource QueryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource QueryCancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? QueryGate { get; init; }

        public TaskCompletionSource? DisposeGate { get; init; }

        public List<string>? DisposalOrder { get; init; }

        public bool IsUnavailable { get; init; }

        public bool WaitForQueryCancellation { get; init; }

        public Exception? DisposeFailure { get; init; }

        public bool IsDisposed { get; private set; }

        public int QueryCallCount => Volatile.Read(ref _queryCallCount);

        public Task StartAsync(TargetProcess target, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(target);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public async Task<ExecutionProfile> GetExecutionProfileAsync(
            ExecutionTimeRange timeRange,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(timeRange);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _queryCallCount);
            QueryStarted.TrySetResult();
            if (IsUnavailable)
            {
                throw new DiagnosticsException(
                    DiagnosticsErrorCode.ExecutionProfilingUnavailable,
                    "Execution sampling is unavailable.");
            }

            if (WaitForQueryCancellation)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    QueryCancellationObserved.TrySetResult();
                    throw;
                }
            }

            if (QueryGate is not null)
            {
                await QueryGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new ExecutionProfile(timeRange, 0, 0, [], []);
        }

        public async ValueTask DisposeAsync()
        {
            DisposalOrder?.Add("execution");
            IsDisposed = true;
            if (WaitForQueryCancellation)
            {
                await QueryCancellationObserved.Task.ConfigureAwait(false);
            }

            DisposeStarted.TrySetResult();
            if (DisposeGate is not null)
            {
                await DisposeGate.Task.ConfigureAwait(false);
            }

            if (DisposeFailure is not null)
            {
                throw DisposeFailure;
            }
        }
    }

    private sealed class ControlledAllocationSamplingSessionResource : IAllocationSamplingSessionResource
    {
        private readonly List<string> _disposalOrder;

        public ControlledAllocationSamplingSessionResource(List<string> disposalOrder)
        {
            _disposalOrder = disposalOrder;
            Collector = new AllocationSamplingSession(
                new AllocationProfileBuilder(Utc("2026-09-02T08:00:00Z")));
        }

        public AllocationSamplingSession Collector { get; }

        public Exception? DisposeFailure { get; init; }

        public bool IsDisposed { get; private set; }

        public Task StartAsync(TargetProcess target, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void MarkInterrupted(DateTimeOffset observedAtUtc)
        {
        }

        public async ValueTask DisposeAsync()
        {
            _disposalOrder.Add("allocation");
            IsDisposed = true;
            await Collector.DisposeAsync().ConfigureAwait(false);
            if (DisposeFailure is not null)
            {
                throw DisposeFailure;
            }
        }
    }

    private sealed class ControlledTerminalEventBus : IEventBus
    {
        private readonly List<IApplicationEvent> _events = [];

        public TaskCompletionSource TerminalPublishStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource TerminalPublishGate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource TerminalPublishCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<IApplicationEvent> Events => _events;

        public async ValueTask PublishAsync<TEvent>(
            TEvent applicationEvent,
            CancellationToken cancellationToken)
            where TEvent : IApplicationEvent
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (applicationEvent is ProcessDiagnosticsSessionEnded)
            {
                TerminalPublishStarted.TrySetResult();
                await TerminalPublishGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            _events.Add(applicationEvent);
            if (applicationEvent is ProcessDiagnosticsSessionEnded)
            {
                TerminalPublishCompleted.TrySetResult();
            }
        }

        public IDisposable Subscribe<TEvent>(
            Func<TEvent, CancellationToken, ValueTask> handler,
            EventSubscriptionOptions? options = null)
            where TEvent : IApplicationEvent => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

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
                : [];
            _entries.Add(new LogEntry(logLevel, eventId, exception, properties));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed record LogEntry(
        LogLevel LogLevel,
        EventId EventId,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class RecordingEventBus : IEventBus
    {
        private readonly List<IApplicationEvent> _events = [];

        public IReadOnlyList<IApplicationEvent> Events => _events;

        public ValueTask PublishAsync<TEvent>(TEvent applicationEvent, CancellationToken cancellationToken)
            where TEvent : IApplicationEvent
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add(applicationEvent);
            return ValueTask.CompletedTask;
        }

        public IDisposable Subscribe<TEvent>(
            Func<TEvent, CancellationToken, ValueTask> handler,
            EventSubscriptionOptions? options = null)
            where TEvent : IApplicationEvent => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
