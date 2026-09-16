using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;
using DotnetAnalysis.Orchestration.DependencyInjection;
using DotnetAnalysis.Orchestration.Models;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetAnalysis.Orchestration.Tests;

/// <summary>验证诊断应用上下文的替换、快照入口、关闭和代次隔离。</summary>
[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe orchestration behavior.")]
public sealed class DiagnosticsApplicationTests
{
    [TestMethod]
    public async Task AttachReplacement_StopsPreviousSessionAndAdvancesGeneration()
    {
        var diagnostics = new FakeDiagnostics();
        await using var application = CreateApplication(diagnostics);
        var first = await application.AttachAsync(Target(1), CancellationToken.None);
        var firstGeneration = application.Generation;

        var second = await application.AttachAsync(Target(2), CancellationToken.None);

        Assert.AreNotEqual(firstGeneration, application.Generation);
        Assert.AreSame(second, application.ActiveSession);
        Assert.AreEqual(1, diagnostics.Sessions[0].EndCallCount);
        Assert.AreEqual(ProcessDiagnosticsSessionState.Ended, diagnostics.Sessions[0].State);
        Assert.AreEqual(Target(2).ProcessId, application.State.Target!.Target.ProcessId);
    }

    [TestMethod]
    public async Task LaunchAndAttach_MapsLaunchTargetAndUsesValidatedIdentity()
    {
        var diagnostics = new FakeDiagnostics();
        var launcher = new FakeLauncher(Target(7));
        await using var application = CreateApplication(diagnostics, launcher);

        var session = await application.LaunchAndAttachAsync(
            new LaunchTarget("sample.exe", "--ready", "C:\\sample", LaunchTargetStrategy.TerminateOnFailure, TimeSpan.FromSeconds(4)),
            CancellationToken.None);

        Assert.AreSame(session, application.ActiveSession);
        Assert.AreEqual("sample.exe", launcher.Request!.ExecutablePath);
        Assert.AreEqual(TargetProcessLaunchFailureStrategy.TerminateProcess, launcher.Request.FailureStrategy);
        Assert.AreEqual(7, session.Target.Target.ProcessId);
    }

    [TestMethod]
    public async Task OpenSnapshot_ReplacesSessionAndReturnsCollectionAnalysis()
    {
        var diagnostics = new FakeDiagnostics();
        await using var application = CreateApplication(diagnostics);
        _ = await application.AttachAsync(Target(3), CancellationToken.None);
        var analysis = await application.OpenSnapshotAsync("sample.gcdump", CancellationToken.None);

        Assert.IsNull(application.ActiveSession);
        Assert.AreEqual(DiagnosticsApplicationPhase.SnapshotSelection, application.State.Phase);
        Assert.AreEqual(diagnostics.ImportedSnapshot.Id, analysis.Snapshot.Id);
        Assert.AreSame(analysis.Snapshot, application.Snapshots.CurrentSnapshot);
        Assert.AreEqual(1, diagnostics.Sessions[0].EndCallCount);
    }

    [TestMethod]
    public async Task Close_IsIdempotentAndPublishesOldSessionGeneration()
    {
        var diagnostics = new FakeDiagnostics();
        var eventBus = new RecordingEventBus();
        await using var application = CreateApplication(diagnostics, eventBus: eventBus);
        _ = await application.AttachAsync(Target(4), CancellationToken.None);
        var sessionGeneration = application.Generation;

        await application.CloseAsync(CancellationToken.None);
        await application.CloseAsync(CancellationToken.None);

        Assert.AreEqual(DiagnosticsApplicationPhase.Closed, application.State.Phase);
        Assert.AreEqual(1, diagnostics.Sessions[0].EndCallCount);
        var ended = eventBus.Events.OfType<ProcessDiagnosticsSessionEnded>().Single();
        Assert.AreEqual(sessionGeneration, ended.Generation);
    }

    [TestMethod]
    public async Task ConcurrentAttach_IsSerializedAndOnlyLatestSessionRemains()
    {
        var diagnostics = new FakeDiagnostics { AttachDelay = TimeSpan.FromMilliseconds(30) };
        await using var application = CreateApplication(diagnostics);

        var firstTask = application.AttachAsync(Target(10), CancellationToken.None);
        var secondTask = application.AttachAsync(Target(11), CancellationToken.None);
        await Task.WhenAll(firstTask, secondTask);

        Assert.HasCount(2, diagnostics.Sessions);
        Assert.AreEqual(1, diagnostics.Sessions[0].EndCallCount);
        Assert.AreEqual(11, application.ActiveSession!.Target.Target.ProcessId);
    }

    private static DiagnosticsApplication CreateApplication(
        FakeDiagnostics diagnostics,
        FakeLauncher? launcher = null,
        RecordingEventBus? eventBus = null) =>
        new(
            diagnostics,
            launcher ?? new FakeLauncher(Target(99)),
            new TargetProcessFinder(diagnostics),
            new TargetCapabilityProbe(diagnostics),
            new FakeAnalysisService(),
            eventBus ?? new RecordingEventBus());

    private static TargetProcess Target(int processId) =>
        new(processId, new DateTimeOffset(2026, 9, 16, 1, 2, 0, TimeSpan.Zero).AddSeconds(processId), $"target-{processId}", null);

    private sealed class FakeDiagnostics : IProcessDiagnostics
    {
        public List<FakeSession> Sessions { get; } = [];
        public TimeSpan AttachDelay { get; init; }
        public MemorySnapshot ImportedSnapshot { get; } = new(MemorySnapshotId.New(), MemorySnapshotOrigin.Imported, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, MemorySnapshotState.Ready);

        public Task<IReadOnlyList<TargetProcess>> GetProcessesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TargetProcess>>([]);

        public Task<TargetProcessCapabilities> ProbeCapabilitiesAsync(TargetProcess process, CancellationToken cancellationToken) =>
            Task.FromResult(new TargetProcessCapabilities(process, true, true, true, true, 10, true, null, null, true, null, null, true, null, null, DateTimeOffset.UtcNow));

        public async Task<IProcessDiagnosticsSession> AttachAsync(TargetProcess process, CancellationToken cancellationToken)
        {
            await Task.Delay(AttachDelay, cancellationToken);
            var session = new FakeSession(process);
            Sessions.Add(session);
            return session;
        }

        public Task<MemorySnapshot> OpenSnapshotAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult(ImportedSnapshot);
    }

    private sealed class FakeSession(TargetProcess process) : IProcessDiagnosticsSession
    {
        public int EndCallCount { get; private set; }
        public ProcessDiagnosticsSessionId Id { get; } = ProcessDiagnosticsSessionId.New();
        public TargetProcess Process { get; } = process;
        public ProcessDiagnosticsSessionState State { get; private set; } = ProcessDiagnosticsSessionState.Monitoring;

        public Task EndAsync(CancellationToken cancellationToken)
        {
            if (State is ProcessDiagnosticsSessionState.Ended) return Task.CompletedTask;
            EndCallCount++;
            State = ProcessDiagnosticsSessionState.Ended;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<MemoryUsageSample> GetMemoryUsageAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<MemorySnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(new MemorySnapshot(MemorySnapshotId.New(), MemorySnapshotOrigin.Captured, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, MemorySnapshotState.Ready));
        public Task<MemorySnapshot> CaptureSnapshotAsync(MemorySnapshotCaptureMode captureMode, CancellationToken cancellationToken) => CaptureSnapshotAsync(cancellationToken);
        public Task<ExecutionProfile> GetExecutionProfileAsync(ExecutionTimeRange timeRange, CancellationToken cancellationToken) => Task.FromException<ExecutionProfile>(new NotSupportedException());
        public Task<ExecutionProfile> GetExecutionProfileAsync(ExecutionTimeRange timeRange, ExecutionProfileQueryMode queryMode, CancellationToken cancellationToken) => Task.FromException<ExecutionProfile>(new NotSupportedException());
        public async ValueTask DisposeAsync() => await EndAsync(CancellationToken.None);
    }

    private sealed class FakeLauncher(TargetProcess target) : ITargetProcessLauncher
    {
        public TargetProcessLaunchRequest? Request { get; private set; }
        public Task<TargetProcessLaunchResult> LaunchAsync(TargetProcessLaunchRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new TargetProcessLaunchResult(target, true));
        }
    }

    private sealed class RecordingEventBus : IEventBus
    {
        public List<IApplicationEvent> Events { get; } = [];
        public ValueTask PublishAsync<TEvent>(TEvent applicationEvent, CancellationToken cancellationToken) where TEvent : IApplicationEvent
        {
            Events.Add(applicationEvent);
            return ValueTask.CompletedTask;
        }
        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler, EventSubscriptionOptions? options = null) where TEvent : IApplicationEvent => new NoopDisposable();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }

    private sealed class FakeAnalysisService : IMemorySnapshotAnalysisService
    {
        public Task<MemorySnapshotAnalysis> AnalyzeAsync(MemorySnapshot snapshot, CancellationToken cancellationToken) => Task.FromResult(new MemorySnapshotAnalysis(snapshot, [], AllocationProfile.NotAvailable(snapshot.RequestedAtUtc, snapshot.RequestedAtUtc)));
        public Task<IReadOnlyList<MemoryObjectInfo>> GetObjectsAsync(MemorySnapshot snapshot, TypeIdentity type, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MemoryObjectInfo>>([]);
        public Task<MemoryObjectPage> GetObjectsPageAsync(MemorySnapshot snapshot, TypeIdentity type, int offset, int pageSize, CancellationToken cancellationToken) => Task.FromResult(new MemoryObjectPage([], 0, offset, pageSize));
        public Task<MemoryReferencePath?> GetReferencePathAsync(MemorySnapshot snapshot, ulong objectAddress, CancellationToken cancellationToken) => Task.FromResult<MemoryReferencePath?>(null);
    }
}
