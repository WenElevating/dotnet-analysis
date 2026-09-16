using System.Diagnostics;
using System.Text.Json;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Orchestration;
using DotnetAnalysis.Orchestration.Models;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

/// <summary>执行真实编排链路的显式性能门禁并保存原始测量证据。</summary>
[TestClass]
[DoNotParallelize]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe orchestration gates.")]
public sealed class OrchestrationPerformanceTests
{
    private const string Gate = "DOTNET_ANALYSIS_RUN_ORCHESTRATION_PERFORMANCE";
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    /// <summary>按对象规模测量查找、附着、三次快照、索引缓存、分页、路径和比较。</summary>
    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    [DataRow(100_000)]
    [DataRow(1_000_000)]
    [DataRow(5_000_000)]
    [DataRow(10_000_000)]
    [Timeout(1_800_000, CooperativeCancellation = true)]
    public async Task ExplicitPerformanceGate_RecordsRealOrchestrationMeasurements(int objectCount)
    {
        RequireEnabled();
        await using var fixture = OrchestratedIntegrationFixture.Create();
        await using var target = await IntegrationTestHost.StartTargetAsync("net10.0", objectCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var token = timeout.Token;
        var measurements = new List<double>();
        var process = Process.GetCurrentProcess();
        var privateMemoryBefore = process.PrivateMemorySize64;
        var candidates = await fixture.Application.FindTargetsAsync(new ProcessFilter(pageSize: 1_000), token);
        var identity = candidates.Single(item => item.ProcessId == target.ProcessId);

        var attachMilliseconds = await MeasureAsync(
            () => fixture.Application.AttachAsync(identity, token),
            measurements);
        var session = fixture.Application.ActiveSession!;
        await Task.Delay(TimeSpan.FromSeconds(2), token);

        var snapshots = new List<MemorySnapshot>(capacity: 3);
        var analyses = new List<ISnapshotAnalysis>(capacity: 3);
        var captureMilliseconds = new List<double>(capacity: 3);
        for (var index = 0; index < 3; index++)
        {
            var capture = Stopwatch.StartNew();
            var snapshot = await fixture.Application.Snapshots.AddCapturedAsync(
                cancellation => session.CaptureAsync(MemorySnapshotCaptureMode.Standard, cancellation),
                token);
            capture.Stop();
            captureMilliseconds.Add(capture.Elapsed.TotalMilliseconds);
            snapshots.Add(snapshot);
            var analysis = fixture.Application.Snapshots.GetAnalysis(snapshot.Id);
            analyses.Add(analysis);
            await MeasureAsync(() => analysis.AnalyzeAsync(token), measurements);
        }

        var firstAnalysis = analyses[0];
        var lastAnalysis = analyses[^1];
        var cachedIndexMilliseconds = await MeasureAsync(() => firstAnalysis.AnalyzeAsync(token), measurements);
        var summary = (await firstAnalysis.AnalyzeAsync(token))
            .Types
            .Where(item => item.ObjectCount > 0)
            .OrderByDescending(item => item.ObjectCount)
            .First();
        var totalObjects = checked((int)summary.ObjectCount);
        var pageMilliseconds = new List<double>();
        pageMilliseconds.Add(await MeasureAsync(() => firstAnalysis.GetObjectsPageAsync(summary.Type, 0, 1_000, token), measurements));
        pageMilliseconds.Add(await MeasureAsync(() => firstAnalysis.GetObjectsPageAsync(summary.Type, Math.Max(0, totalObjects / 2), 1_000, token), measurements));
        pageMilliseconds.Add(await MeasureAsync(() => firstAnalysis.GetObjectsPageAsync(summary.Type, Math.Max(0, totalObjects - 1_000), 1_000, token), measurements));
        var emptyPage = await firstAnalysis.GetObjectsPageAsync(summary.Type, totalObjects, 1_000, token);
        Assert.IsEmpty(emptyPage.Objects);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => firstAnalysis.GetObjectsPageAsync(summary.Type, -1, 1_000, token));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => firstAnalysis.GetObjectsPageAsync(summary.Type, totalObjects + 1, 1_000, token));

        var tailPage = await firstAnalysis.GetObjectsPageAsync(summary.Type, Math.Max(0, totalObjects - 1), 1, token);
        Assert.IsNotEmpty(tailPage.Objects);
        var referencePath = await firstAnalysis.GetReferencePathAsync(tailPage.Objects[0].Address, token);
        var comparison = firstAnalysis.CompareWith(lastAnalysis);
        var comparisonResult = await comparison.AnalyzeAsync(token);
        Assert.AreEqual(snapshots[0].Id, comparisonResult.BaselineSnapshotId);
        Assert.AreEqual(snapshots[^1].Id, comparisonResult.CandidateSnapshotId);

        var concurrentPages = Enumerable.Range(0, 8)
            .Select(index => firstAnalysis.GetObjectsPageAsync(summary.Type, Math.Min(index * 1_000, totalObjects), 1_000, token))
            .ToArray();
        await Task.WhenAll(concurrentPages);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => firstAnalysis.GetObjectsPageAsync(summary.Type, 0, 1, canceled.Token));

        var snapshotPath = fixture.SnapshotLayout.GetFinalDumpPath(snapshots[^1].Id);
        var fileSize = new FileInfo(snapshotPath).Length;
        await session.StopAsync(CancellationToken.None);
        var snapshotRoot = fixture.SnapshotRoot;
        await fixture.DisposeAsync();
        Assert.AreEqual(DiagnosticsApplicationPhase.Closed, fixture.Application.State.Phase);
        Assert.IsFalse(Directory.Exists(snapshotRoot));
        var privateMemoryAfter = process.PrivateMemorySize64;
        WriteEvidence(new
        {
            objectCount,
            targetFramework = "net10.0",
            targetPid = target.ProcessId,
            targetStartedAtUtc = identity.StartedAtUtc,
            attachMilliseconds,
            captureMilliseconds,
            cachedIndexMilliseconds,
            pageMilliseconds,
            fileSize,
            privateMemoryBefore,
            privateMemoryAfter,
            privateMemoryDelta = privateMemoryAfter - privateMemoryBefore,
            referencePathFound = referencePath is not null,
            snapshotIds = snapshots.Select(item => item.Id).ToArray(),
            rawMilliseconds = measurements,
            p50Milliseconds = Percentile(measurements, 0.50),
            p95Milliseconds = Percentile(measurements, 0.95),
            p99Milliseconds = Percentile(measurements, 0.99),
            resourcesCleaned = fixture.Application.State.Phase == DiagnosticsApplicationPhase.Closed && !Directory.Exists(snapshotRoot)
        });
    }

    private static async Task<double> MeasureAsync<T>(Func<Task<T>> operation, List<double> measurements)
    {
        var stopwatch = Stopwatch.StartNew();
        _ = await operation().ConfigureAwait(false);
        stopwatch.Stop();
        measurements.Add(stopwatch.Elapsed.TotalMilliseconds);
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static async Task<double> MeasureAsync(Func<Task> operation, List<double> measurements)
    {
        var stopwatch = Stopwatch.StartNew();
        await operation().ConfigureAwait(false);
        stopwatch.Stop();
        measurements.Add(stopwatch.Elapsed.TotalMilliseconds);
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        var index = Math.Clamp((int)Math.Ceiling(ordered.Length * percentile) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private static void RequireEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(Gate), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive($"Set {Gate}=true to run this explicit long-running gate.");
        }
    }

    private static void WriteEvidence(object evidence)
    {
        var directory = Environment.GetEnvironmentVariable("DOTNET_ANALYSIS_ORCHESTRATION_EVIDENCE")
            ?? Path.Combine("TestResults", $"OrchestrationAcceptance-{DateTime.UtcNow:yyyyMMdd-HHmmss}", "performance");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"performance-{Guid.NewGuid():N}.json"), JsonSerializer.Serialize(evidence, s_jsonOptions));
    }
}
