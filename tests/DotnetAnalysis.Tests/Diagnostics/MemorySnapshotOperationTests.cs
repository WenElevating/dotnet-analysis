using System.Globalization;
using System.Diagnostics.CodeAnalysis;
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
public sealed class MemorySnapshotOperationTests
{
    [TestMethod]
    public async Task RetryAnalysisAsync_DoesNotCaptureAgain()
    {
        var snapshot = Snapshot(MemorySnapshotState.Analyzing);
        var analysisService = new ControlledSnapshotAnalysisService(analysisFailuresBeforeSuccess: 1);
        var operation = CreateOperation(snapshot, analysisService);

        await Assert.ThrowsAsync<DiagnosticsException>(
            async () => await operation.AnalyzeAsync(CancellationToken.None));
        var analysis = await operation.RetryAnalysisAsync(CancellationToken.None);

        Assert.AreEqual(MemorySnapshotState.Ready, operation.State);
        Assert.AreEqual(snapshot.Id, operation.Snapshot.Id);
        Assert.AreEqual(snapshot.Id, analysis.Snapshot.Id);
        Assert.AreEqual(2, analysisService.AnalyzeCalls);
    }

    [TestMethod]
    public async Task RetryAnalysisAsync_WhenOperationHasNotFailed_Rejects()
    {
        var operation = CreateOperation(
            Snapshot(MemorySnapshotState.Analyzing),
            new ControlledSnapshotAnalysisService());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await operation.RetryAnalysisAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task AnalyzeAsync_WhenSuccessful_MovesToReadyAndReturnsStableAnalysisSnapshot()
    {
        var snapshot = Snapshot(MemorySnapshotState.Analyzing);
        var operation = CreateOperation(snapshot, new ControlledSnapshotAnalysisService());

        var analysis = await operation.AnalyzeAsync(CancellationToken.None);

        Assert.AreEqual(MemorySnapshotState.Ready, operation.State);
        Assert.AreEqual(MemorySnapshotState.Ready, operation.Snapshot.State);
        Assert.AreEqual(operation.Snapshot, analysis.Snapshot);
    }

    [TestMethod]
    public async Task AnalyzeAsync_WhenUnexpectedExceptionOccurs_LogsSnapshotStageAndWrapsStableErrorCode()
    {
        var innerException = new InvalidOperationException("reader failed");
        var logger = new RecordingLogger<MemorySnapshotOperation>();
        var operation = CreateOperation(
            Snapshot(MemorySnapshotState.Analyzing),
            new ControlledSnapshotAnalysisService(unexpectedException: innerException),
            logger);

        var exception = await Assert.ThrowsAsync<DiagnosticsException>(
            async () => await operation.AnalyzeAsync(CancellationToken.None));

        Assert.AreEqual(DiagnosticsErrorCode.CaptureFailed, exception.ErrorCode);
        Assert.AreSame(innerException, exception.InnerException);
        Assert.AreEqual(MemorySnapshotState.Failed, operation.State);

        var log = logger.Entries.Single(entry => entry.EventId.Name == "MemorySnapshotStageFailed");
        Assert.AreEqual(operation.Snapshot.Id, log.Properties["SnapshotId"]);
        Assert.AreEqual("Analyze", log.Properties["Stage"]);
        Assert.AreEqual(DiagnosticsErrorCode.CaptureFailed, log.Properties["ErrorCode"]);
        Assert.AreSame(innerException, log.Exception);
    }

    [TestMethod]
    public async Task GetObjectsAsync_AndGetReferencePathAsync_ReadThroughSameAnalysisService()
    {
        var type = new TypeIdentity("Sample.Type", "Sample");
        var expectedObject = new MemoryObjectInfo(42, type, 64);
        var expectedPath = new MemoryReferencePath(42, [expectedObject]);
        var analysisService = new ControlledSnapshotAnalysisService
        {
            Objects = [expectedObject],
            ReferencePath = expectedPath
        };
        var operation = CreateOperation(Snapshot(MemorySnapshotState.Ready), analysisService);

        var objects = await operation.GetObjectsAsync(type, CancellationToken.None);
        var referencePath = await operation.GetReferencePathAsync(42, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { expectedObject }, objects.ToArray());
        Assert.AreEqual(expectedPath, referencePath);
        Assert.AreEqual(1, analysisService.GetObjectsCalls);
        Assert.AreEqual(1, analysisService.GetReferencePathCalls);
    }

    [TestMethod]
    public async Task GetRetentionPathsAsync_ReadsRootEvidenceThroughAnalysisService()
    {
        var type = new TypeIdentity("Sample.Type", "Sample");
        var expected = new MemoryRetentionPathResult(
            42,
            [
                new MemoryRetentionPath(
                    new MemoryRetentionRoot(
                        MemoryRootKind.Stack,
                        MemoryRootFlags.StackRoot,
                        "Sample.Holder.KeepAlive",
                        "Sample"),
                    [new MemoryObjectInfo(42, type, 64)])
            ]);
        var analysisService = new ControlledSnapshotAnalysisService { RetentionPaths = expected };
        var operation = CreateOperation(Snapshot(MemorySnapshotState.Ready), analysisService);

        var result = await operation.GetRetentionPathsAsync(42, 16, CancellationToken.None);

        Assert.AreEqual(expected, result);
        Assert.AreEqual(1, analysisService.GetRetentionPathsCalls);
    }

    [TestMethod]
    public async Task GetObjectsPageAsync_ReadsRequestedPageThroughAnalysisService()
    {
        var type = new TypeIdentity("Sample.Type", "Sample");
        var expected = new MemoryObjectPage([new MemoryObjectInfo(42, type, 64)], 3, 1, 1);
        var analysisService = new ControlledSnapshotAnalysisService { ObjectPage = expected };
        var operation = CreateOperation(Snapshot(MemorySnapshotState.Ready), analysisService);

        var page = await operation.GetObjectsPageAsync(type, 1, 1, CancellationToken.None);

        Assert.AreEqual(expected, page);
        Assert.AreEqual(1, analysisService.GetObjectsPageCalls);
    }

    [TestMethod]
    public async Task AnalyzeAsync_PublishesSnapshotLifecycleEvents()
    {
        var eventBus = new RecordingEventBus();
        var sessionId = ProcessDiagnosticsSessionId.New();
        var operation = CreateOperation(
            Snapshot(MemorySnapshotState.Analyzing),
            new ControlledSnapshotAnalysisService(),
            eventBus,
            sessionId);

        await operation.AnalyzeAsync(CancellationToken.None);

        Assert.AreEqual(1, eventBus.Events.OfType<MemorySnapshotAnalysisStarted>().Count());
        Assert.AreEqual(1, eventBus.Events.OfType<MemorySnapshotAnalysisCompleted>().Count());
        Assert.AreEqual(sessionId, eventBus.Events.OfType<MemorySnapshotAnalysisStarted>().Single().SessionId);
        Assert.AreEqual(sessionId, eventBus.Events.OfType<MemorySnapshotAnalysisCompleted>().Single().SessionId);
    }

    private static MemorySnapshotOperation CreateOperation(
        MemorySnapshot snapshot,
        ControlledSnapshotAnalysisService analysisService,
        ILogger<MemorySnapshotOperation>? logger = null)
    {
        return new MemorySnapshotOperation(
            snapshot,
            analysisService,
            TimeProvider.System,
            logger ?? NullLogger<MemorySnapshotOperation>.Instance);
    }

    private static MemorySnapshotOperation CreateOperation(
        MemorySnapshot snapshot,
        ControlledSnapshotAnalysisService analysisService,
        RecordingEventBus eventBus,
        ProcessDiagnosticsSessionId sessionId,
        ILogger<MemorySnapshotOperation>? logger = null)
    {
        return new MemorySnapshotOperation(
            snapshot,
            analysisService,
            eventBus,
            sessionId,
            TimeProvider.System,
            logger ?? NullLogger<MemorySnapshotOperation>.Instance);
    }

    private static MemorySnapshot Snapshot(MemorySnapshotState state) =>
        new(
            MemorySnapshotId.New(),
            MemorySnapshotOrigin.Captured,
            Utc("2026-09-02T08:00:05Z"),
            Utc("2026-09-02T08:00:06Z"),
            Utc("2026-09-02T08:00:07Z"),
            state);

    private static DateTimeOffset Utc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private sealed class ControlledSnapshotAnalysisService(
        int analysisFailuresBeforeSuccess = 0,
        Exception? unexpectedException = null) : IMemorySnapshotAnalysisService
    {
        private int _remainingAnalysisFailures = analysisFailuresBeforeSuccess;

        public int AnalyzeCalls { get; private set; }

        public int GetObjectsCalls { get; private set; }

        public int GetReferencePathCalls { get; private set; }

        public int GetObjectsPageCalls { get; private set; }

        public int GetRetentionPathsCalls { get; private set; }

        public IReadOnlyList<MemoryObjectInfo> Objects { get; init; } = [];

        public MemoryReferencePath? ReferencePath { get; init; }

        public MemoryObjectPage ObjectPage { get; init; } = new([], 0, 0, 1);

        public MemoryRetentionPathResult? RetentionPaths { get; init; }

        public Task<MemorySnapshotAnalysis> AnalyzeAsync(MemorySnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AnalyzeCalls++;

            if (unexpectedException is not null)
            {
                throw unexpectedException;
            }

            if (_remainingAnalysisFailures > 0)
            {
                _remainingAnalysisFailures--;
                throw new DiagnosticsException(DiagnosticsErrorCode.CaptureFailed, "Analysis failed.");
            }

            var readySnapshot = new MemorySnapshot(
                snapshot.Id,
                snapshot.Origin,
                snapshot.RequestedAtUtc,
                snapshot.CaptureStartedAtUtc,
                snapshot.CapturedAtUtc,
                MemorySnapshotState.Ready);
            var analysis = new MemorySnapshotAnalysis(
                readySnapshot,
                Array.Empty<MemoryTypeSummary>(),
                AllocationProfile.NotAvailable(snapshot.RequestedAtUtc, snapshot.CapturedAtUtc ?? snapshot.RequestedAtUtc));
            return Task.FromResult(analysis);
        }

        public Task<IReadOnlyList<MemoryObjectInfo>> GetObjectsAsync(
            MemorySnapshot snapshot,
            TypeIdentity type,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetObjectsCalls++;
            return Task.FromResult(Objects);
        }

        public Task<MemoryReferencePath?> GetReferencePathAsync(
            MemorySnapshot snapshot,
            ulong objectAddress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetReferencePathCalls++;
            return Task.FromResult(ReferencePath);
        }

        public Task<MemoryRetentionPathResult?> GetRetentionPathsAsync(
            MemorySnapshot snapshot,
            ulong objectAddress,
            int maxPathCount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetRetentionPathsCalls++;
            return Task.FromResult(RetentionPaths);
        }

        public Task<MemoryObjectPage> GetObjectsPageAsync(
            MemorySnapshot snapshot,
            TypeIdentity type,
            int offset,
            int pageSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetObjectsPageCalls++;
            return Task.FromResult(ObjectPage);
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
