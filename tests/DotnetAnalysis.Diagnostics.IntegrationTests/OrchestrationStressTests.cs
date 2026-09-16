using System.Diagnostics;
using System.Text.Json;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Orchestration;
using DotnetAnalysis.Orchestration.Models;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

/// <summary>执行显式开启的重复、并发和长时编排压力流程。</summary>
[TestClass]
[DoNotParallelize]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe orchestration gates.")]
public sealed class OrchestrationStressTests
{
    private const string Gate = "DOTNET_ANALYSIS_RUN_ORCHESTRATION_STRESS";
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    /// <summary>连续执行二十轮打开、附着、采样、快照、索引、查询和释放。</summary>
    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    [Timeout(7_200_000, CooperativeCancellation = true)]
    public async Task ExplicitStressGate_RunsTwentyResourceCycles()
    {
        RequireEnabled();
        using var timeout = new CancellationTokenSource(TimeSpan.FromHours(2));
        var measurements = new List<CycleMeasurement>(capacity: 20);
        var privateMemoryBefore = Process.GetCurrentProcess().PrivateMemorySize64;
        for (var cycle = 0; cycle < 20; cycle++)
        {
            measurements.Add(await RunCycleAsync(cycle, timeout.Token));
        }

        var privateMemoryAfter = Process.GetCurrentProcess().PrivateMemorySize64;
        WriteEvidence(new
        {
            cycles = measurements,
            privateMemoryBefore,
            privateMemoryAfter,
            privateMemoryDelta = privateMemoryAfter - privateMemoryBefore,
            allResourcesCleaned = measurements.All(item => item.ResourcesCleaned)
        }, "stress-cycles");
        Assert.IsTrue(measurements.All(item => item.ResourcesCleaned));
    }

    /// <summary>验证分页、引用路径和取消请求可并发执行，且一个调用取消不取消其他查询。</summary>
    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    [Timeout(600_000, CooperativeCancellation = true)]
    public async Task ExplicitStressGate_ConcurrentQueriesAndCancellationRemainIsolated()
    {
        RequireEnabled();
        await using var fixture = OrchestratedIntegrationFixture.Create();
        await using var target = await IntegrationTestHost.StartTargetAsync("net10.0", 100_000);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var candidates = await fixture.Application.FindTargetsAsync(new ProcessFilter(pageSize: 1_000), timeout.Token);
        var identity = candidates.Single(item => item.ProcessId == target.ProcessId);
        var session = await fixture.Application.AttachAsync(identity, timeout.Token);
        var snapshot = await fixture.Application.Snapshots.AddCapturedAsync(token => session.CaptureAsync(MemorySnapshotCaptureMode.Standard, token), timeout.Token);
        var analysis = fixture.Application.Snapshots.GetAnalysis(snapshot.Id);
        var result = await analysis.AnalyzeAsync(timeout.Token);
        var type = result.Types.OrderByDescending(item => item.ObjectCount).First();
        var page = await analysis.GetObjectsPageAsync(type.Type, 0, 64, timeout.Token);
        Assert.IsNotEmpty(page.Objects);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var canceledQuery = Assert.ThrowsAsync<OperationCanceledException>(() => analysis.GetObjectsPageAsync(type.Type, 0, 64, canceled.Token));
        var concurrentQueries = Enumerable.Range(0, 8)
            .Select(index => analysis.GetObjectsPageAsync(type.Type, Math.Min(index * 64, (int)page.TotalObjectCount), 64, timeout.Token))
            .ToArray();
        await Task.WhenAll(concurrentQueries);
        await canceledQuery;
        var path = await analysis.GetReferencePathAsync(page.Objects[0].Address, timeout.Token);
        await session.StopAsync(CancellationToken.None);
        var snapshotRoot = fixture.SnapshotRoot;
        await fixture.DisposeAsync();
        Assert.AreEqual(DiagnosticsApplicationPhase.Closed, fixture.Application.State.Phase);
        Assert.IsFalse(Directory.Exists(snapshotRoot));
        WriteEvidence(new { targetPid = target.ProcessId, snapshotId = snapshot.Id, queriedPageCount = concurrentQueries.Length, referencePathFound = path is not null, resourcesCleaned = !Directory.Exists(snapshotRoot) }, "stress-concurrency");
    }

    /// <summary>执行显式开启的六十分钟执行采样基准。</summary>
    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    [Timeout(4_200_000, CooperativeCancellation = true)]
    public async Task ExplicitStressGate_ExecutionSamplingSixtyMinuteBaseline()
    {
        RequireEnabled();
        await RunLongSamplingAsync(TimeSpan.FromMinutes(60), soak: false);
    }

    /// <summary>执行显式开启的两小时执行采样 Soak，并交错查询、取消和周期快照。</summary>
    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    [Timeout(8_400_000, CooperativeCancellation = true)]
    public async Task ExplicitStressGate_ExecutionSamplingTwoHourSoak()
    {
        RequireEnabled();
        await RunLongSamplingAsync(TimeSpan.FromHours(2), soak: true);
    }

    private static async Task<CycleMeasurement> RunCycleAsync(int cycle, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var fixture = OrchestratedIntegrationFixture.Create();
        await using var target = await IntegrationTestHost.StartTargetAsync("net10.0", 100_000);
        var candidates = await fixture.Application.FindTargetsAsync(new ProcessFilter(pageSize: 1_000), cancellationToken);
        var identity = candidates.Single(item => item.ProcessId == target.ProcessId);
        var session = await fixture.Application.AttachAsync(identity, cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        var snapshot = await fixture.Application.Snapshots.AddCapturedAsync(token => session.CaptureAsync(MemorySnapshotCaptureMode.Standard, token), cancellationToken);
        var analysis = fixture.Application.Snapshots.GetAnalysis(snapshot.Id);
        var result = await analysis.AnalyzeAsync(cancellationToken);
        var type = result.Types.OrderByDescending(item => item.ObjectCount).First();
        var page = await analysis.GetObjectsPageAsync(type.Type, 0, 64, cancellationToken);
        if (page.Objects.Count > 0)
        {
            _ = await analysis.GetReferencePathAsync(page.Objects[0].Address, cancellationToken);
        }

        await session.StopAsync(CancellationToken.None);
        var snapshotRoot = fixture.SnapshotRoot;
        await fixture.DisposeAsync();
        stopwatch.Stop();
        var resourcesCleaned = fixture.Application.State.Phase == DiagnosticsApplicationPhase.Closed && !Directory.Exists(snapshotRoot);
        return new CycleMeasurement(cycle, stopwatch.Elapsed.TotalMilliseconds, target.ProcessId, snapshot.Id, resourcesCleaned);
    }

    private static async Task RunLongSamplingAsync(TimeSpan duration, bool soak)
    {
        await using var fixture = OrchestratedIntegrationFixture.Create();
        await using var target = await IntegrationTestHost.StartTargetAsync("net10.0", new IntegrationTargetOptions(InitialObjectCount: 1_024, EnableExecutionWorkload: true));
        using var timeout = new CancellationTokenSource(duration + TimeSpan.FromMinutes(5));
        var candidates = await fixture.Application.FindTargetsAsync(new ProcessFilter(pageSize: 1_000), timeout.Token);
        var identity = candidates.Single(item => item.ProcessId == target.ProcessId);
        var session = await fixture.Application.AttachAsync(identity, timeout.Token);
        var start = DateTimeOffset.UtcNow;
        var samples = new List<SamplingMeasurement>();
        var random = new Random(0x5EED);
        while (DateTimeOffset.UtcNow - start < duration)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), timeout.Token);
            var timeline = session.Timeline.Snapshot();
            ExecutionProfile? profile = null;
            if (timeline.Count >= 2)
            {
                var range = new ExecutionTimeRange(timeline[0].ObservedAtUtc, timeline[^1].ObservedAtUtc);
                try { profile = await session.GetExecutionProfileAsync(range, ExecutionProfileQueryMode.Incremental, timeout.Token); }
                catch (DiagnosticsException exception) when (exception.ErrorCode == DiagnosticsErrorCode.ExecutionProfileRangeUnavailable) { }
            }

            if (soak && random.Next(0, 5) == 0)
            {
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                try { await session.CaptureAsync(MemorySnapshotCaptureMode.Standard, canceled.Token); }
                catch (DiagnosticsException exception) when (exception.ErrorCode == DiagnosticsErrorCode.CaptureCancelled) { }
            }

            samples.Add(new SamplingMeasurement(DateTimeOffset.UtcNow, timeline.Count, session.Quality.Quality, profile?.ReceivedSampleCount ?? 0, profile?.LostEventCount ?? 0, Process.GetCurrentProcess().PrivateMemorySize64));
        }

        await session.StopAsync(CancellationToken.None);
        var snapshotRoot = fixture.SnapshotRoot;
        await fixture.DisposeAsync();
        Assert.AreEqual(DiagnosticsApplicationPhase.Closed, fixture.Application.State.Phase);
        Assert.IsFalse(Directory.Exists(snapshotRoot));
        WriteEvidence(new { targetPid = target.ProcessId, duration, soak, samples, resourcesCleaned = !Directory.Exists(snapshotRoot) }, soak ? "stress-soak" : "stress-sampling");
    }

    private static void RequireEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(Gate), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive($"Set {Gate}=true to run this explicit stress gate.");
        }
    }

    private static void WriteEvidence(object evidence, string name)
    {
        var directory = Environment.GetEnvironmentVariable("DOTNET_ANALYSIS_ORCHESTRATION_EVIDENCE")
            ?? Path.Combine("TestResults", $"OrchestrationAcceptance-{DateTime.UtcNow:yyyyMMdd-HHmmss}", "stress");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{name}-{Guid.NewGuid():N}.json"), JsonSerializer.Serialize(evidence, s_jsonOptions));
    }

    private sealed record CycleMeasurement(int Cycle, double ElapsedMilliseconds, int TargetPid, MemorySnapshotId SnapshotId, bool ResourcesCleaned);
    private sealed record SamplingMeasurement(DateTimeOffset AtUtc, int TimelineCount, DiagnosticQuality Quality, long ReceivedSampleCount, long LostEventCount, long PrivateMemoryBytes);
}
