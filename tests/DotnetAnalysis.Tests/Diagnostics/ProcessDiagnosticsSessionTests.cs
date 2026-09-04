using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;
using DotnetAnalysis.Diagnostics.Windows;
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
    public async Task EndAsync_CancelsInFlightCaptureAndReleasesOwnedResourceOnce()
    {
        var capture = new ControlledCapture { WaitForCancellation = true };
        var resource = new TrackingAsyncDisposable();
        await using var session = CreateSession(capture, ownedResource: resource);

        var activeCapture = session.CaptureSnapshotAsync(CancellationToken.None);
        await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await session.EndAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        await session.DisposeAsync();

        var exception = await Assert.ThrowsAsync<DiagnosticsException>(
            async () => await activeCapture.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.AreEqual(DiagnosticsErrorCode.CaptureCancelled, exception.ErrorCode);
        Assert.AreEqual(ProcessDiagnosticsSessionState.Ended, session.State);
        Assert.AreEqual(1, resource.DisposeCalls);
    }

    [TestMethod]
    public async Task GetMemoryUsageAsync_WhenSessionEndedSampleArrives_MovesToEnded()
    {
        var endedProcess = new TargetProcess(int.MaxValue, Utc("2026-09-02T08:00:00Z"), "ended-target", null);
        var sampler = new ProcessMemorySampler(
            endedProcess,
            new ProcessMemoryReader(),
            new UnavailableManagedHeapReader(),
            TimeProvider.System,
            TimeSpan.Zero);
        await using var session = new ProcessDiagnosticsSession(
            endedProcess,
            sampler,
            new RecordingEventBus(),
            TimeProvider.System,
            NullLogger<ProcessDiagnosticsSession>.Instance);

        await foreach (var _ in session.GetMemoryUsageAsync(CancellationToken.None))
        {
        }

        Assert.AreEqual(ProcessDiagnosticsSessionState.Ended, session.State);
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

        foreach (var errorCode in Enum.GetValues<DiagnosticsErrorCode>())
        {
            var exception = new DiagnosticsException(errorCode, "stable", inner);

            Assert.AreEqual(errorCode, exception.ErrorCode);
            Assert.AreEqual("stable", exception.Message);
            Assert.AreSame(inner, exception.InnerException);
        }
    }

    private ProcessDiagnosticsSession CreateSession(
        ControlledCapture capture,
        IEventBus? eventBus = null,
        IAsyncDisposable? ownedResource = null,
        ILogger<ProcessDiagnosticsSession>? logger = null)
    {
        var sampler = new ProcessMemorySampler(
            _process,
            new UnavailableProcessMemoryReader(),
            new UnavailableManagedHeapReader(),
            TimeProvider.System,
            TimeSpan.Zero);
        return new ProcessDiagnosticsSession(
            _process,
            sampler,
            eventBus ?? new RecordingEventBus(),
            TimeProvider.System,
            logger ?? NullLogger<ProcessDiagnosticsSession>.Instance,
            capture.CaptureAsync,
            ownedResource);
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

    private sealed class ControlledCapture
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? CaptureGate { get; init; }

        public Exception? Failure { get; set; }

        public int Calls { get; private set; }

        public bool WaitForCancellation { get; init; }

        public async Task<MemorySnapshot> CaptureAsync(CancellationToken cancellationToken)
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

    private sealed class TrackingAsyncDisposable : IAsyncDisposable
    {
        public int DisposeCalls { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
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
