using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Orchestration.Models;

namespace DotnetAnalysis.Orchestration.Tests;

/// <summary>验证活动分析会话的所有权、时间线和操作收尾。</summary>
[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe orchestration behavior.")]
public sealed class AnalysisSessionTests
{
    /// <summary>验证附着后消费样本、转发捕获和执行查询，并可幂等停止。</summary>
    [TestMethod]
    public async Task Attach_ConsumesSamplesForwardsOperationsAndStopsIdempotently()
    {
        var target = CreateTargetContext(retentionAvailable: true);
        var fakeSession = new FakeDiagnosticsSession(target.Target);
        var diagnostics = new FakeProcessDiagnostics(fakeSession);
        await using var session = await AnalysisSession.AttachAsync(diagnostics, target, CancellationToken.None, timelineCapacity: 2);

        await WaitUntilAsync(() => session.Timeline.Count == 2);
        Assert.AreEqual(ProcessDiagnosticsSessionState.Monitoring, session.State);
        Assert.AreEqual(2, session.Timeline.Count);
        Assert.AreEqual(DiagnosticQuality.Complete, session.Quality.Quality);

        var range = new ExecutionTimeRange(
            fakeSession.Samples[0].ObservedAtUtc,
            fakeSession.Samples[1].ObservedAtUtc);
        var profile = await session.GetExecutionProfileAsync(range, ExecutionProfileQueryMode.Incremental, CancellationToken.None);
        Assert.AreEqual(range, profile.TimeRange);

        await session.CaptureAsync(MemorySnapshotCaptureMode.Standard, CancellationToken.None);
        await session.CaptureAsync(MemorySnapshotCaptureMode.RetentionAnalysis, CancellationToken.None);
        CollectionAssert.AreEqual(
            new[] { MemorySnapshotCaptureMode.Standard, MemorySnapshotCaptureMode.RetentionAnalysis },
            fakeSession.CaptureModes);

        await session.StopAsync(CancellationToken.None);
        await session.StopAsync(CancellationToken.None);
        Assert.AreEqual(ProcessDiagnosticsSessionState.Ended, session.State);
        Assert.AreEqual(1, fakeSession.EndCallCount);
    }

    /// <summary>验证 Retention 能力不可用时不会降级为标准捕获。</summary>
    [TestMethod]
    public async Task CaptureRetention_WhenUnavailableFailsWithoutFallback()
    {
        var target = CreateTargetContext(retentionAvailable: false);
        var fakeSession = new FakeDiagnosticsSession(target.Target);
        var diagnostics = new FakeProcessDiagnostics(fakeSession);
        await using var session = await AnalysisSession.AttachAsync(diagnostics, target, CancellationToken.None);

        var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
            () => session.CaptureAsync(MemorySnapshotCaptureMode.RetentionAnalysis, CancellationToken.None));

        Assert.AreEqual(DiagnosticsErrorCode.ProfilerAttachUnavailable, exception.ErrorCode);
        CollectionAssert.DoesNotContain(fakeSession.CaptureModes, MemorySnapshotCaptureMode.Standard);
    }

    /// <summary>验证时间线容量限制会保留缺失质量而不无限增长。</summary>
    [TestMethod]
    public void Timeline_IsBoundedAndPreservesMissingQuality()
    {
        var timeline = new MemoryTimeline(2);
        timeline.Append(new MemoryUsageSample(DateTimeOffset.UtcNow, 1, 2, MemoryUsageSampleState.Measured));
        timeline.Append(new MemoryUsageSample(DateTimeOffset.UtcNow.AddSeconds(1), null, null, MemoryUsageSampleState.Unavailable));
        timeline.Append(new MemoryUsageSample(DateTimeOffset.UtcNow.AddSeconds(2), null, null, MemoryUsageSampleState.SessionEnded));

        Assert.AreEqual(2, timeline.Count);
        Assert.AreEqual(DiagnosticQuality.Partial, timeline.Quality.Quality);
        CollectionAssert.Contains(timeline.Quality.MissingEvidence.ToArray(), "部分采样不可用");
        CollectionAssert.Contains(timeline.Quality.MissingEvidence.ToArray(), "目标会话已结束");
    }

    /// <summary>验证执行查询区间超出已观察时间线时返回稳定错误码。</summary>
    [TestMethod]
    public async Task ExecutionProfile_RejectsRangeOutsideObservedTimeline()
    {
        var target = CreateTargetContext(retentionAvailable: true);
        var fakeSession = new FakeDiagnosticsSession(target.Target);
        var diagnostics = new FakeProcessDiagnostics(fakeSession);
        await using var session = await AnalysisSession.AttachAsync(diagnostics, target, CancellationToken.None);
        await WaitUntilAsync(() => session.Timeline.Count == 2);

        var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(() => session.GetExecutionProfileAsync(
            new ExecutionTimeRange(
                fakeSession.Samples[0].ObservedAtUtc.AddSeconds(-1),
                fakeSession.Samples[1].ObservedAtUtc),
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None));

        Assert.AreEqual(DiagnosticsErrorCode.ExecutionProfileRangeUnavailable, exception.ErrorCode);
    }

    private static TargetContext CreateTargetContext(bool retentionAvailable)
    {
        var target = new TargetProcess(
            42,
            new DateTimeOffset(2026, 9, 16, 1, 2, 3, TimeSpan.Zero),
            "analysis-target",
            null);
        var capabilities = new DiagnosticCapabilities(
            new DiagnosticCapabilityAvailability(true),
            new DiagnosticCapabilityAvailability(
                retentionAvailable,
                retentionAvailable ? null : "Profiler unavailable",
                retentionAvailable ? null : DiagnosticsErrorCode.ProfilerAttachUnavailable),
            new DiagnosticCapabilityAvailability(true),
            new DiagnosticCapabilityAvailability(true),
            new DiagnosticCapabilityAvailability(true),
            DateTimeOffset.UtcNow);
        return new TargetContext(target, ".NET 10", TargetArchitecture.X64, capabilities);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("Timed out waiting for the fake sample stream.");
    }

    private sealed class FakeProcessDiagnostics : IProcessDiagnostics
    {
        private readonly FakeDiagnosticsSession _session;

        public FakeProcessDiagnostics(FakeDiagnosticsSession session) => _session = session;

        public Task<IReadOnlyList<TargetProcess>> GetProcessesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TargetProcess>>([_session.Process]);

        public Task<IProcessDiagnosticsSession> AttachAsync(TargetProcess process, CancellationToken cancellationToken) =>
            Task.FromResult<IProcessDiagnosticsSession>(_session);

        public Task<MemorySnapshot> OpenSnapshotAsync(string filePath, CancellationToken cancellationToken) =>
            Task.FromException<MemorySnapshot>(new DiagnosticsException(DiagnosticsErrorCode.SnapshotFormatNotSupported, "Not used."));
    }

    private sealed class FakeDiagnosticsSession : IProcessDiagnosticsSession
    {
        private readonly CancellationTokenSource _endCancellation = new();
        private int _endCallCount;

        public FakeDiagnosticsSession(TargetProcess process)
        {
            Process = process;
            Samples =
            [
                new MemoryUsageSample(new DateTimeOffset(2026, 9, 16, 1, 2, 4, TimeSpan.Zero), 10, 20, MemoryUsageSampleState.Measured),
                new MemoryUsageSample(new DateTimeOffset(2026, 9, 16, 1, 2, 5, TimeSpan.Zero), 11, 21, MemoryUsageSampleState.Measured)
            ];
        }

        public ProcessDiagnosticsSessionId Id { get; } = ProcessDiagnosticsSessionId.New();
        public TargetProcess Process { get; }
        public ProcessDiagnosticsSessionState State => _endCancellation.IsCancellationRequested
            ? ProcessDiagnosticsSessionState.Ended
            : ProcessDiagnosticsSessionState.Monitoring;
        public IReadOnlyList<MemoryUsageSample> Samples { get; }
        public List<MemorySnapshotCaptureMode> CaptureModes { get; } = [];
        public int EndCallCount => _endCallCount;

        public async Task EndAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _endCallCount);
            _endCancellation.Cancel();
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<MemoryUsageSample> GetMemoryUsageAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _endCancellation.Token);
            foreach (var sample in Samples)
            {
                linked.Token.ThrowIfCancellationRequested();
                yield return sample;
                await Task.Yield();
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
        }

        public Task<MemorySnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken) =>
            CaptureSnapshotAsync(MemorySnapshotCaptureMode.Standard, cancellationToken);

        public Task<MemorySnapshot> CaptureSnapshotAsync(MemorySnapshotCaptureMode captureMode, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureModes.Add(captureMode);
            return Task.FromResult(new MemorySnapshot(
                MemorySnapshotId.New(),
                MemorySnapshotOrigin.Captured,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                MemorySnapshotState.Ready));
        }

        public Task<ExecutionProfile> GetExecutionProfileAsync(ExecutionTimeRange timeRange, CancellationToken cancellationToken) =>
            Task.FromResult(new ExecutionProfile(timeRange, 1, 0, [], []));

        public Task<ExecutionProfile> GetExecutionProfileAsync(ExecutionTimeRange timeRange, ExecutionProfileQueryMode queryMode, CancellationToken cancellationToken) =>
            GetExecutionProfileAsync(timeRange, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
