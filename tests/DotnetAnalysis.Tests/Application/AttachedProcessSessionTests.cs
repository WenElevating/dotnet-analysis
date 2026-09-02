using System.Collections;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Application.Sessions;
using DotnetAnalysis.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetAnalysis.Tests.Application;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class AttachedProcessSessionTests
{
    private readonly TargetProcess _process = new(4567, Utc("2026-09-02T08:00:00Z"), "target", null);

    [TestMethod]
    public async Task CaptureSnapshotAsync_RejectsConcurrentCapture()
    {
        var captureGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnosticSession = new ControlledDiagnosticsSession(_process) { CaptureGate = captureGate };
        await using var session = CreateSession(diagnosticSession);

        var firstCapture = session.CaptureSnapshotAsync(CancellationToken.None);
        await diagnosticSession.CaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await session.CaptureSnapshotAsync(CancellationToken.None));

        captureGate.TrySetResult();
        var snapshot = await firstCapture.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(MemorySnapshotState.Analyzing, snapshot.State);
        Assert.AreEqual(ProcessDiagnosticsSessionState.Monitoring, session.State);
        Assert.AreEqual(1, diagnosticSession.CaptureCalls);
    }

    [TestMethod]
    public async Task CaptureSnapshotAsync_WhenCaptureIsCancelled_ClearsCaptureGateForRetry()
    {
        var diagnosticSession = new ControlledDiagnosticsSession(_process)
        {
            CaptureException = new DiagnosticsException(DiagnosticsErrorCode.CaptureCancelled, "Capture cancelled.")
        };
        await using var session = CreateSession(diagnosticSession);

        var exception = await Assert.ThrowsAsync<DiagnosticsException>(
            async () => await session.CaptureSnapshotAsync(CancellationToken.None));

        Assert.AreEqual(DiagnosticsErrorCode.CaptureCancelled, exception.ErrorCode);
        Assert.AreEqual(ProcessDiagnosticsSessionState.Monitoring, session.State);

        diagnosticSession.CaptureException = null;
        var snapshot = await session.CaptureSnapshotAsync(CancellationToken.None);

        Assert.AreEqual(MemorySnapshotState.Analyzing, snapshot.State);
        Assert.AreEqual(2, diagnosticSession.CaptureCalls);
    }

    [TestMethod]
    public async Task EndAsync_CancelsInFlightCaptureAndDisposesInnerSessionOnce()
    {
        var diagnosticSession = new ControlledDiagnosticsSession(_process)
        {
            WaitForCaptureCancellation = true
        };
        await using var session = CreateSession(diagnosticSession);

        var capture = session.CaptureSnapshotAsync(CancellationToken.None);
        await diagnosticSession.CaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await session.EndAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        await session.DisposeAsync();

        var exception = await Assert.ThrowsAsync<DiagnosticsException>(
            async () => await capture.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.AreEqual(DiagnosticsErrorCode.CaptureCancelled, exception.ErrorCode);
        Assert.AreEqual(ProcessDiagnosticsSessionState.Ended, session.State);
        Assert.AreEqual(1, diagnosticSession.DisposeCalls);
    }

    [TestMethod]
    public async Task GetMemoryUsageAsync_WhenSessionEndedSampleArrives_MovesToEnded()
    {
        var diagnosticSession = new ControlledDiagnosticsSession(
            _process,
            new MemoryUsageSample(
                Utc("2026-09-02T08:01:00Z"),
                null,
                null,
                MemoryUsageSampleState.SessionEnded));
        await using var session = CreateSession(diagnosticSession);

        await foreach (var _ in session.GetMemoryUsageAsync(CancellationToken.None))
        {
        }

        Assert.AreEqual(ProcessDiagnosticsSessionState.Ended, session.State);
    }

    [TestMethod]
    public async Task CaptureSnapshotAsync_WhenUnexpectedExceptionOccurs_LogsStructuredStageAndWrapsStableErrorCode()
    {
        var innerException = new InvalidOperationException("boom");
        var diagnosticSession = new ControlledDiagnosticsSession(_process)
        {
            CaptureException = innerException
        };
        var logger = new RecordingLogger<AttachedProcessSession>();
        await using var session = CreateSession(diagnosticSession, logger);

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

    private static AttachedProcessSession CreateSession(
        ControlledDiagnosticsSession diagnosticSession,
        ILogger<AttachedProcessSession>? logger = null)
    {
        return new AttachedProcessSession(
            ProcessDiagnosticsSessionId.New(),
            diagnosticSession,
            TimeProvider.System,
            logger ?? NullLogger<AttachedProcessSession>.Instance);
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

    private sealed class ControlledDiagnosticsSession(
        TargetProcess process,
        params MemoryUsageSample[] samples) : IProcessDiagnosticsSession
    {
        public ProcessDiagnosticsSessionId Id { get; } = ProcessDiagnosticsSessionId.New();

        public TargetProcess Process { get; } = process;

        public ProcessDiagnosticsSessionState State { get; private set; } = ProcessDiagnosticsSessionState.Monitoring;

        public Task EndAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = ProcessDiagnosticsSessionState.Ended;
            return Task.CompletedTask;
        }

        public TaskCompletionSource CaptureStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? CaptureGate { get; init; }

        public Exception? CaptureException { get; set; }

        public int CaptureCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public bool WaitForCaptureCancellation { get; init; }

        public async Task<MemorySnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken)
        {
            CaptureCalls++;
            CaptureStarted.TrySetResult();

            if (WaitForCaptureCancellation)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new DiagnosticsException(DiagnosticsErrorCode.CaptureCancelled, "Capture cancelled.");
                }
            }

            if (CaptureGate is not null)
            {
                await CaptureGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (CaptureException is not null)
            {
                throw CaptureException;
            }

            return Snapshot();
        }

        public async IAsyncEnumerable<MemoryUsageSample> GetMemoryUsageAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var sample in samples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return sample;
                await Task.Yield();
            }
        }

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
}
