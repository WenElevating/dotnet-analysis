using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class ExecutionSamplingPerformanceTests
{
    private const string BenchmarkGate = "DOTNET_ANALYSIS_RUN_EXECUTION_PROFILE_BENCHMARK";
    private const string DualModeDifferentialGate = "DOTNET_ANALYSIS_RUN_EXECUTION_PROFILE_DUAL_MODE_DIFFERENTIAL";
    private const string StressGate = "DOTNET_ANALYSIS_RUN_EXECUTION_PROFILE_STRESS";
    private const string AttributionProbeGate = "DOTNET_ANALYSIS_RUN_EXECUTION_PROFILE_ATTRIBUTION";
    private const string TargetFramework = "net10.0";
    private const string SamplingConfiguration = "runtime Sample Profiler default; no interval override configured";
    private const int WorkerCount = 8;
    private const int PathCount = 200;
    private const int ThroughputRoundCount = 3;
    private const int FullRangeQueryCount = 10;
    private const int ConcurrentQueryCount = 8;
    private const int ConcurrentCancellationCount = 2;
    private const int MaximumConcurrentIncrementalProfileBuilds = 2;
    private const int SnapshotSequenceCount = 3;
    private const int AttributionQueryCount = 10;
    private const int BenchmarkRandomSeed = 0x51A7;
    private const int StressRandomSeed = 0x57E55;
    private const long MaximumPrivateMemoryIncreaseBytes = 128L * 1024 * 1024;
    private const long MaximumStorageBytes = 128L * 1024 * 1024;
    private const long MaximumQueryAllocationBytes = 64L * 1024 * 1024;
    // 全栈调用树采样的运行时 provider 开销记在目标进程中；保留一个明确的
    // 全会话预算，而不是把诊断端 CPU 预算错误地用于约束该成本。
    private const double MaximumTargetThroughputDropPercent = 25;
    private const double MaximumDiagnosticCoreUsage = 0.05;
    private const double MaximumQueryP95Milliseconds = 2_000;
    private static readonly TimeSpan s_warmupDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_throughputRoundDuration = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan s_benchmarkMeasurementDuration = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan s_dualModeDifferentialMeasurementDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan s_stressMeasurementDuration = TimeSpan.FromHours(2);
    private static readonly TimeSpan s_resourceInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan s_availableRangeSafetyMargin = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_availableRangeStartOffset = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_snapshotRecoveryDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan s_finalContinuityObservationDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_cleanupStepTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_attributionWarmupDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_attributionMeasurementDuration = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions s_attributionJsonOptions = new() { WriteIndented = true };
    private static readonly ExecutionSamplingAttributionMode[][] s_attributionOrders =
    [
        [
            ExecutionSamplingAttributionMode.Unattached,
            ExecutionSamplingAttributionMode.ExecutionOnly,
            ExecutionSamplingAttributionMode.FullAttach
        ],
        [
            ExecutionSamplingAttributionMode.ExecutionOnly,
            ExecutionSamplingAttributionMode.FullAttach,
            ExecutionSamplingAttributionMode.Unattached
        ],
        [
            ExecutionSamplingAttributionMode.FullAttach,
            ExecutionSamplingAttributionMode.Unattached,
            ExecutionSamplingAttributionMode.ExecutionOnly
        ]
    ];
    private static readonly string[] s_expectedFailedThresholds = ["fails"];
    private readonly TestContext _testContext;

    public ExecutionSamplingPerformanceTests(TestContext testContext) => _testContext = testContext;

    [TestMethod]
    public async Task ExecutionSamplingEvidenceWriter_WriteAsync_WhenSuccessfulRunHasNoRawMeasurements_RejectsSummary()
    {
        var artifactRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var writer = ExecutionSamplingEvidenceWriter.Create(
                artifactRoot,
                "benchmark",
                new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero));
            var evidence = ExecutionSamplingRunEvidence.Create("benchmark");
            PopulateTraceabilityContext(evidence, evidence.StartedAtUtc);
            evidence.Thresholds.Add(
                "sampleThreshold",
                new ExecutionSamplingThresholdDecision(true, "==", 1, 1, "count"));

            var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(
                async () => await writer.WriteAsync(evidence, CancellationToken.None));

            StringAssert.Contains(exception.Message, "raw measurement arrays");
            Assert.IsFalse(File.Exists(writer.EvidencePath));
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(artifactRoot);
        }
    }

    [TestMethod]
    public async Task ExecutionSamplingEvidenceWriter_WriteAsync_WhenRunFails_WritesCompleteRawArraySchema()
    {
        var artifactRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var writer = ExecutionSamplingEvidenceWriter.Create(
                artifactRoot,
                "stress",
                new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero));
            var evidence = ExecutionSamplingRunEvidence.Create("stress");
            evidence.Exceptions.Add(ExecutionSamplingExceptionEvidence.From(
                "setup",
                new InvalidOperationException("controlled failure")));

            await writer.WriteAsync(evidence, CancellationToken.None);

            var json = await File.ReadAllTextAsync(writer.EvidencePath);
            StringAssert.Contains(json, "\"receivedSampleCounts\": []");
            StringAssert.Contains(json, "\"targetThroughput\": []");
            StringAssert.Contains(json, "\"diagnosticResources\": []");
            StringAssert.Contains(json, "\"minuteCadenceLagSeconds\": []");
            StringAssert.Contains(json, "\"tenMinuteCheckpointLagSeconds\": []");
            StringAssert.Contains(json, "\"queries\": []");
            StringAssert.Contains(json, "\"concurrentQueries\": []");
            StringAssert.Contains(json, "\"snapshots\": []");
            StringAssert.Contains(json, "controlled failure");
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(artifactRoot);
        }
    }

    [TestMethod]
    public async Task ExecutionSamplingEvidenceWriter_WriteAsync_WhenSuccessfulRunLacksTraceabilityContext_RejectsEvidence()
    {
        var artifactRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var startedAtUtc = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
            var writer = ExecutionSamplingEvidenceWriter.Create(
                artifactRoot,
                "benchmark",
                startedAtUtc);
            var evidence = CreateSuccessfulEvidenceWithRawMeasurements(startedAtUtc);

            var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(
                async () => await writer.WriteAsync(evidence, CancellationToken.None));

            StringAssert.Contains(exception.Message, nameof(evidence.Environment));
            StringAssert.Contains(exception.Message, nameof(evidence.Target));
            StringAssert.Contains(exception.Message, nameof(evidence.Configuration));
            StringAssert.Contains(exception.Message, nameof(evidence.CompletedAtUtc));
            Assert.IsFalse(File.Exists(writer.EvidencePath));
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(artifactRoot);
        }
    }

    [TestMethod]
    public async Task ExecutionSamplingEvidenceWriter_WriteAsync_WhenSuccessfulStressLacksCadenceArrays_RejectsEvidence()
    {
        var artifactRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var startedAtUtc = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
            var writer = ExecutionSamplingEvidenceWriter.Create(artifactRoot, "stress", startedAtUtc);
            var evidence = CreateSuccessfulEvidenceWithRawMeasurements(startedAtUtc, "stress");
            PopulateTraceabilityContext(evidence, startedAtUtc);

            var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(
                async () => await writer.WriteAsync(evidence, CancellationToken.None));

            StringAssert.Contains(exception.Message, nameof(evidence.MinuteCadenceLagSeconds));
            StringAssert.Contains(exception.Message, nameof(evidence.TenMinuteCheckpointLagSeconds));
            Assert.IsFalse(File.Exists(writer.EvidencePath));
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(artifactRoot);
        }
    }

    [TestMethod]
    public async Task ExecutionSamplingEvidenceWriter_WriteAsync_WhenFailureContainsNonFiniteThreshold_WritesEvidence()
    {
        var artifactRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var writer = ExecutionSamplingEvidenceWriter.Create(
                artifactRoot,
                "benchmark",
                new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero));
            var evidence = ExecutionSamplingRunEvidence.Create("benchmark");
            evidence.Exceptions.Add(ExecutionSamplingExceptionEvidence.From(
                "measurement",
                new InvalidOperationException("controlled failure")));
            evidence.Thresholds.Add(
                "zeroBaseline",
                new ExecutionSamplingThresholdDecision(
                    false,
                    "<=",
                    double.PositiveInfinity,
                    MaximumTargetThroughputDropPercent,
                    "percent"));

            await writer.WriteAsync(evidence, CancellationToken.None);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(writer.EvidencePath));
            var actual = document.RootElement
                .GetProperty("thresholds")
                .GetProperty("zeroBaseline")
                .GetProperty("actual");
            Assert.AreEqual(JsonValueKind.String, actual.ValueKind);
            Assert.AreEqual("Infinity", actual.GetString());
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(artifactRoot);
        }
    }

    [TestMethod]
    public void ExecutionSamplingThresholds_GetTargetThroughputDropPercent_WhenBaselineIsZero_ReturnsFiniteFailure()
    {
        var dropPercent = ExecutionSamplingThresholds.GetTargetThroughputDropPercent(0, 10);

        Assert.AreEqual(100d, dropPercent);
        Assert.IsTrue(double.IsFinite(dropPercent));
    }

    [TestMethod]
    public void ExecutionSamplingThroughputDrop_UsesActualDurationNormalizedRates()
    {
        var timestamp = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
        var measurements = new[]
        {
            Throughput("unattached", 1, 120, 1_200),
            Throughput("attached", 1, 100, 1_000),
            Throughput("unattached", 2, 118, 1_180),
            Throughput("attached", 2, 90, 900),
            Throughput("unattached", 3, 122, 1_220),
            Throughput("attached", 3, 110, 1_100)
        };

        var dropPercent = CalculateTargetThroughputDropPercent(measurements);

        Assert.AreEqual(0d, dropPercent, 0.000_001d);

        ExecutionSamplingThroughputMeasurement Throughput(
            string mode,
            int sequence,
            double durationSeconds,
            long completedOperations) =>
            new(
                mode,
                sequence,
                123,
                timestamp,
                timestamp,
                timestamp.AddSeconds(durationSeconds),
                durationSeconds,
                0,
                completedOperations,
                completedOperations);
    }

    [TestMethod]
    public void ExecutionSamplingMedian_WhenMeasurementCountIsEven_AveragesMiddleValues()
    {
        var median = Median([30, 10, 40, 20]);

        Assert.AreEqual(25d, median);
    }

    [TestMethod]
    public void ExecutionSamplingThresholds_IsUsableSnapshot_RequiresAnalyzingSnapshotAndNonEmptyDump()
    {
        var valid = new ExecutionSamplingSnapshotMeasurement(
            1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            1,
            Guid.NewGuid().ToString("N"),
            MemorySnapshotState.Analyzing.ToString(),
            1,
            1,
            1,
            1,
            1,
            1,
            0,
            true,
            null);

        Assert.IsTrue(ExecutionSamplingThresholds.IsUsableSnapshot(valid));
        Assert.IsFalse(ExecutionSamplingThresholds.IsUsableSnapshot(valid with { FileSizeBytes = 0 }));
        Assert.IsFalse(ExecutionSamplingThresholds.IsUsableSnapshot(valid with { FileSizeBytes = null }));
        Assert.IsFalse(ExecutionSamplingThresholds.IsUsableSnapshot(valid with { State = MemorySnapshotState.Failed.ToString() }));
    }

    [TestMethod]
    public void ExecutionSamplingThresholds_GetFailures_WhenDecisionFails_ReturnsHardFailure()
    {
        var thresholds = new Dictionary<string, ExecutionSamplingThresholdDecision>(StringComparer.Ordinal)
        {
            ["passes"] = new(true, "<=", 4, 5, "milliseconds"),
            ["fails"] = new(false, "<=", 6, 5, "milliseconds")
        };

        var failures = ExecutionSamplingThresholds.GetFailures(thresholds);

        CollectionAssert.AreEqual(s_expectedFailedThresholds, failures);
    }

    [TestMethod]
    public void ExecutionSamplingGate_IsEnabled_OnlyForExplicitTrue()
    {
        Assert.IsTrue(ExecutionSamplingGate.IsEnabled("true"));
        Assert.IsTrue(ExecutionSamplingGate.IsEnabled("TRUE"));
        Assert.IsFalse(ExecutionSamplingGate.IsEnabled(null));
        Assert.IsFalse(ExecutionSamplingGate.IsEnabled(string.Empty));
        Assert.IsFalse(ExecutionSamplingGate.IsEnabled("1"));
    }

    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    [DoNotParallelize]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task ControlledExecutionWorkloadTarget_ReadCompletedOperations_ObservesPublishedRealWorkloadCount()
    {
        EnsureNet10RuntimeIsInstalled();
        await using var preparedTarget = await PreparedExecutionWorkloadTarget.CreateAsync(
            _testContext.CancellationToken);
        await using var target = await preparedTarget.StartAsync(_testContext.CancellationToken);

        var before = await target.ReadCompletedOperationsAsync(_testContext.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(250), _testContext.CancellationToken);
        var after = await target.ReadCompletedOperationsAsync(_testContext.CancellationToken);

        Assert.IsGreaterThan(before, after);
        Assert.IsTrue(Path.IsPathFullyQualified(target.ExecutablePath));
        Assert.IsTrue(File.Exists(target.WorkloadAssemblyPath));
    }

    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    [DoNotParallelize]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task AttachedExecutionFixture_SmokeRun_ValidatesConcurrencySnapshotAndFinalSamplerState()
    {
        EnsureNet10RuntimeIsInstalled();
        await using var preparedTarget = await PreparedExecutionWorkloadTarget.CreateAsync(
            _testContext.CancellationToken);
        await using var fixture = await AttachedExecutionFixture.StartAsync(
            preparedTarget,
            _testContext.CancellationToken);
        var range = await CreateLatestRangeAsync(
            fixture,
            TimeSpan.FromSeconds(2),
            _testContext.CancellationToken);

        var evidence = ExecutionSamplingRunEvidence.Create("benchmark");
        var profile = await MeasureQueryAsync(
            evidence,
            fixture.Session,
            range,
            "smoke",
            1,
            _testContext.CancellationToken);
        await RunConcurrentQueryBatchAsync(
            evidence,
            fixture,
            range,
            1,
            BenchmarkRandomSeed,
            _testContext.CancellationToken);
        await CaptureSnapshotWithContinuityAsync(
            evidence,
            fixture,
            1,
            _testContext.CancellationToken);
        EnsureLatestSnapshotIsUsable(evidence, 1);
        await fixture.EndAsync(_testContext.CancellationToken);

        Assert.IsGreaterThan(0L, profile.ReceivedSampleCount);
        Assert.AreEqual(0L, profile.LostEventCount);
        Assert.HasCount(1, evidence.ConcurrentQueries);
        Assert.HasCount(1, evidence.Snapshots);
        Assert.IsGreaterThan(0L, fixture.FinalSuccessfulSampleCount);
        Assert.AreEqual(0L, fixture.FinalLostEventCount);
        Assert.IsFalse(Directory.Exists(fixture.SessionDirectory));
    }

    [TestMethod]
    [TestCategory("ExecutionSamplingLongRunning")]
    [DoNotParallelize]
    [Timeout(420_000, CooperativeCancellation = true)]
    public async Task ExecutionSamplingAttributionProbe_ExplicitGate_SeparatesExecutionFromFullAttachOverhead()
    {
        RequireExplicitGate(AttributionProbeGate, "execution sampling attribution probe");
        EnsureNet10RuntimeIsInstalled();
        await using var preparedTarget = await PreparedExecutionWorkloadTarget.CreateAsync(
            _testContext.CancellationToken);
        var measurements = new List<ExecutionSamplingAttributionMeasurement>();
        for (var roundIndex = 0; roundIndex < s_attributionOrders.Length; roundIndex++)
        {
            var order = s_attributionOrders[roundIndex];
            for (var positionIndex = 0; positionIndex < order.Length; positionIndex++)
            {
                measurements.Add(await MeasureAttributionAsync(
                    order[positionIndex],
                    preparedTarget,
                    roundIndex + 1,
                    positionIndex + 1,
                    _testContext.CancellationToken));
            }
        }

        var summary = CreateAttributionSummary(measurements);
        var evidence = new ExecutionSamplingAttributionEvidence(measurements, summary);
        var evidenceDirectory = Path.Combine(
            FindRepositoryRoot(),
            "TestResults",
            $"ExecutionSamplingAttribution-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}");
        Directory.CreateDirectory(evidenceDirectory);
        var evidencePath = Path.Combine(evidenceDirectory, "attribution.json");
        await File.WriteAllTextAsync(
            evidencePath,
            JsonSerializer.Serialize(evidence, s_attributionJsonOptions),
            _testContext.CancellationToken);
        _testContext.WriteLine($"Execution sampling attribution evidence: {evidencePath}");

        Assert.HasCount(9, measurements);
        Assert.IsTrue(measurements.All(static measurement => measurement.TargetOperationsPerSecond > 0));
        Assert.IsTrue(measurements
            .Where(static measurement => measurement.Mode != "unattached")
            .All(static measurement => measurement.SuccessfulSampleCount > 0));
        Assert.IsTrue(measurements
            .Where(static measurement => measurement.Mode != "unattached")
            .All(static measurement => measurement.LostEventCount == 0));
        Assert.IsTrue(measurements
            .Where(static measurement => measurement.Mode != "unattached")
            .All(static measurement => measurement.CachedFullRangeQueryMilliseconds.Count == AttributionQueryCount));
    }

    [TestMethod]
    [TestCategory("ExecutionSamplingLongRunning")]
    [DoNotParallelize]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task ExecutionSamplingProviderOnlyAttributionProbe_ExplicitGate_IsolatesRuntimeCaptureOverhead()
    {
        RequireExplicitGate(AttributionProbeGate, "execution sampling provider-only attribution probe");
        EnsureNet10RuntimeIsInstalled();
        await using var preparedTarget = await PreparedExecutionWorkloadTarget.CreateAsync(
            _testContext.CancellationToken);
        var measurements = new List<ExecutionSamplingAttributionMeasurement>
        {
            await MeasureUnattachedAttributionAsync(
                preparedTarget,
                round: 1,
                orderPosition: 1,
                _testContext.CancellationToken),
            await MeasureProviderOnlyAttributionAsync(
                preparedTarget,
                round: 1,
                orderPosition: 2,
                _testContext.CancellationToken),
            await MeasureProviderOnlyAttributionAsync(
                preparedTarget,
                round: 2,
                orderPosition: 1,
                _testContext.CancellationToken),
            await MeasureUnattachedAttributionAsync(
                preparedTarget,
                round: 2,
                orderPosition: 2,
                _testContext.CancellationToken)
        };
        var baselinesByRound = measurements
            .Where(static measurement => measurement.Mode == "unattached")
            .ToDictionary(static measurement => measurement.Round);
        var pairedDrops = CalculatePairedAttributionDrops(
            measurements.Where(static measurement => measurement.Mode == "provider-only").ToArray(),
            baselinesByRound);
        var evidence = new ExecutionSamplingProviderOnlyAttributionEvidence(
            "runtime-provider-session-only; no consumer during measurement; raw stream drained only during cleanup",
            EventPipeExecutionSampler.CreateDefaultProviders()
                .Select(static provider => new ExecutionSamplingProviderEvidence(
                    provider.Name,
                    provider.EventLevel,
                    provider.Keywords))
                .ToArray(),
            EventPipeExecutionSampler.DefaultRequestRundown,
            EventPipeExecutionSampler.DefaultCircularBufferMegabytes,
            measurements,
            pairedDrops,
            Median((double[])pairedDrops.Clone()));
        var evidenceDirectory = Path.Combine(
            FindRepositoryRoot(),
            "TestResults",
            $"ExecutionSamplingProviderOnlyAttribution-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}");
        Directory.CreateDirectory(evidenceDirectory);
        var evidencePath = Path.Combine(evidenceDirectory, "provider-only-attribution.json");
        await File.WriteAllTextAsync(
            evidencePath,
            JsonSerializer.Serialize(evidence, s_attributionJsonOptions),
            _testContext.CancellationToken);
        _testContext.WriteLine($"Execution sampling provider-only attribution evidence: {evidencePath}");

        Assert.HasCount(4, measurements);
        Assert.IsTrue(measurements.All(static measurement => measurement.TargetOperationsPerSecond > 0));
        Assert.HasCount(2, pairedDrops);
    }

    [TestMethod]
    [TestCategory("ExecutionSamplingLongRunning")]
    [DoNotParallelize]
    [Timeout(7_200_000, CooperativeCancellation = true)]
    public async Task ExecutionSamplingBenchmark_ExplicitGate_MeetsSixtyMinuteHardThresholdsAndWritesRawEvidence()
    {
        RequireExplicitGate(BenchmarkGate, "60-minute benchmark");
        EnsureNet10RuntimeIsInstalled();
        await ExecuteEvidenceRunAsync("benchmark", RunBenchmarkAsync, _testContext.CancellationToken);
    }

    [TestMethod]
    [TestCategory("ExecutionSamplingLongRunning")]
    [DoNotParallelize]
    [Timeout(1_500_000, CooperativeCancellation = true)]
    public async Task ExecutionSamplingDualModeDifferential_ExplicitGate_FifteenMinutesProducesIdenticalProfiles()
    {
        RequireExplicitGate(DualModeDifferentialGate, "15-minute FullScan/Incremental differential run");
        EnsureNet10RuntimeIsInstalled();

        await using var preparedTarget = await PreparedExecutionWorkloadTarget.CreateAsync(_testContext.CancellationToken);
        await using var fixture = await AttachedExecutionFixture.StartAsync(preparedTarget, _testContext.CancellationToken);
        await Task.Delay(s_dualModeDifferentialMeasurementDuration, _testContext.CancellationToken);

        var range = new ExecutionTimeRange(
            fixture.AttachedAtUtc.Add(s_availableRangeStartOffset),
            DateTimeOffset.UtcNow.Subtract(s_availableRangeSafetyMargin));
        var fullStopwatch = Stopwatch.StartNew();
        var fullScan = await fixture.Session.GetExecutionProfileAsync(
            range,
            ExecutionProfileQueryMode.FullScan,
            _testContext.CancellationToken);
        fullStopwatch.Stop();
        var incrementalStopwatch = Stopwatch.StartNew();
        var incremental = await fixture.Session.GetExecutionProfileAsync(
            range,
            ExecutionProfileQueryMode.Incremental,
            _testContext.CancellationToken);
        incrementalStopwatch.Stop();

        var fullJson = JsonSerializer.Serialize(fullScan);
        var incrementalJson = JsonSerializer.Serialize(incremental);
        Assert.AreEqual(fullJson, incrementalJson, "FullScan and Incremental profiles must be field-for-field identical.");
        Assert.IsGreaterThan(0L, fullScan.ReceivedSampleCount, "The fixed 15-minute range must contain captured samples.");
        Assert.AreEqual(0L, fullScan.LostEventCount, "The captured EventPipe stream must not report lost events.");

        var evidenceDirectory = Path.Combine(
            FindRepositoryRoot(),
            "TestResults",
            $"ExecutionSamplingDualMode-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}");
        Directory.CreateDirectory(evidenceDirectory);
        var evidencePath = Path.Combine(evidenceDirectory, "dual-mode-differential.json");
        await File.WriteAllTextAsync(
            evidencePath,
            JsonSerializer.Serialize(new
            {
                rangeStartAtUtc = range.StartAtUtc,
                rangeEndAtUtc = range.EndAtUtc,
                receivedSampleCount = fullScan.ReceivedSampleCount,
                lostEventCount = fullScan.LostEventCount,
                fullScanMilliseconds = fullStopwatch.Elapsed.TotalMilliseconds,
                incrementalMilliseconds = incrementalStopwatch.Elapsed.TotalMilliseconds,
                profilesAreIdentical = true
            }, s_attributionJsonOptions),
            _testContext.CancellationToken);

        _testContext.WriteLine(
            $"15-minute dual-mode differential passed. Samples={fullScan.ReceivedSampleCount}; "
            + $"FullScan={fullStopwatch.Elapsed.TotalMilliseconds:F3} ms; "
            + $"Incremental={incrementalStopwatch.Elapsed.TotalMilliseconds:F3} ms; Evidence={evidencePath}.");
    }

    [TestMethod]
    [TestCategory("ExecutionSamplingLongRunning")]
    [DoNotParallelize]
    [Timeout(10_800_000, CooperativeCancellation = true)]
    public async Task ExecutionSamplingStress_ExplicitGate_CompletesTwoHourSoakAndWritesRawEvidence()
    {
        RequireExplicitGate(StressGate, "2-hour stress soak");
        EnsureNet10RuntimeIsInstalled();
        await ExecuteEvidenceRunAsync("stress", RunStressAsync, _testContext.CancellationToken);
    }

    private async Task ExecuteEvidenceRunAsync(
        string runKind,
        Func<ExecutionSamplingRunEvidence, CancellationToken, Task> runAsync,
        CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var writer = ExecutionSamplingEvidenceWriter.Create(runKind, startedAtUtc);
        var evidence = ExecutionSamplingRunEvidence.Create(runKind);
        evidence.StartedAtUtc = startedAtUtc;
        ExceptionDispatchInfo? failure = null;
        try
        {
            evidence.Environment = await CaptureEnvironmentAsync(cancellationToken);
            await runAsync(evidence, cancellationToken);
        }
        catch (Exception exception)
        {
            evidence.Exceptions.Add(ExecutionSamplingExceptionEvidence.From($"{runKind}-run", exception));
            failure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            evidence.CompletedAtUtc = DateTimeOffset.UtcNow;
            await writer.WriteAsync(evidence, CancellationToken.None);
            _testContext.WriteLine($"Execution sampling evidence: {writer.EvidencePath}");
        }

        failure?.Throw();
        var failedThresholds = ExecutionSamplingThresholds.GetFailures(evidence.Thresholds);
        if (failedThresholds.Length > 0)
        {
            Assert.Fail(
                $"Execution sampling {runKind} hard thresholds failed: {string.Join(", ", failedThresholds)}. "
                + $"Evidence: {writer.EvidencePath}");
        }
    }

    private static async Task RunBenchmarkAsync(
        ExecutionSamplingRunEvidence evidence,
        CancellationToken cancellationToken)
    {
        evidence.Configuration = new ExecutionSamplingConfigurationEvidence(
            WorkerCount,
            PathCount,
            SamplingConfiguration,
            s_warmupDuration.TotalSeconds,
            s_benchmarkMeasurementDuration.TotalSeconds,
            s_throughputRoundDuration.TotalSeconds,
            s_resourceInterval.TotalSeconds,
            BenchmarkRandomSeed);

        await using var preparedTarget = await PreparedExecutionWorkloadTarget.CreateAsync(cancellationToken);
        for (var round = 1; round <= ThroughputRoundCount; round++)
        {
            evidence.TargetThroughput.Add(await MeasureUnattachedThroughputAsync(preparedTarget, round, cancellationToken));
            evidence.TargetThroughput.Add(await MeasureAttachedThroughputAsync(
                preparedTarget,
                round,
                evidence,
                cancellationToken));
        }

        await using var fixture = await AttachedExecutionFixture.StartAsync(preparedTarget, cancellationToken);
        evidence.Target = fixture.CreateTargetEvidence();
        await Task.Delay(s_warmupDuration, cancellationToken);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        using var diagnosticsProcess = Process.GetCurrentProcess();
        var measurementStartedAtUtc = DateTimeOffset.UtcNow;
        var startingCompletedOperations = await fixture.Target.ReadCompletedOperationsAsync(cancellationToken);
        var previousCompletedOperations = startingCompletedOperations;
        var previousObservedAtUtc = measurementStartedAtUtc;
        var previousProcessorTime = ReadProcessorTime(diagnosticsProcess);
        var firstResource = CaptureResourceMeasurement(
            "sampling",
            diagnosticsProcess,
            fixture.SessionDirectory,
            measurementStartedAtUtc,
            measurementStartedAtUtc,
            previousProcessorTime,
            previousObservedAtUtc,
            startingCompletedOperations);
        evidence.DiagnosticResources.Add(firstResource);
        evidence.StorageSizeBytes.Add(firstResource.StorageBytes);

        for (var interval = 1; interval <= 60; interval++)
        {
            await DelayUntilAsync(
                measurementStartedAtUtc.AddTicks(s_resourceInterval.Ticks * interval),
                cancellationToken);
            var completedOperations = await fixture.Target.ReadCompletedOperationsAsync(cancellationToken);
            var observedAtUtc = DateTimeOffset.UtcNow;
            var resource = CaptureResourceMeasurement(
                "sampling",
                diagnosticsProcess,
                fixture.SessionDirectory,
                measurementStartedAtUtc,
                previousObservedAtUtc,
                previousProcessorTime,
                observedAtUtc,
                completedOperations);
            evidence.DiagnosticResources.Add(resource);
            evidence.StorageSizeBytes.Add(resource.StorageBytes);
            evidence.TargetThroughput.Add(new ExecutionSamplingThroughputMeasurement(
                "stable-attached-minute",
                interval,
                fixture.Target.ProcessId,
                fixture.Target.StartTimeUtc,
                previousObservedAtUtc,
                resource.ObservedAtUtc,
                (resource.ObservedAtUtc - previousObservedAtUtc).TotalSeconds,
                previousCompletedOperations,
                completedOperations,
                completedOperations - previousCompletedOperations));

            previousObservedAtUtc = resource.ObservedAtUtc;
            previousProcessorTime = TimeSpan.FromMilliseconds(resource.TotalProcessorMilliseconds);
            previousCompletedOperations = completedOperations;
        }

        var measurementCompletedAtUtc = evidence.DiagnosticResources[^1].ObservedAtUtc;
        evidence.TargetThroughput.Add(new ExecutionSamplingThroughputMeasurement(
            "stable-attached-total",
            1,
            fixture.Target.ProcessId,
            fixture.Target.StartTimeUtc,
            measurementStartedAtUtc,
            measurementCompletedAtUtc,
            (measurementCompletedAtUtc - measurementStartedAtUtc).TotalSeconds,
            startingCompletedOperations,
            previousCompletedOperations,
            previousCompletedOperations - startingCompletedOperations));

        var fullRange = new ExecutionTimeRange(
            fixture.AttachedAtUtc.Add(s_availableRangeStartOffset),
            measurementCompletedAtUtc.Subtract(s_availableRangeSafetyMargin));
        await MeasureQueryAsync(evidence, fixture.Session, fullRange, "cold-full-range", 0, cancellationToken);
        for (var query = 1; query <= FullRangeQueryCount; query++)
        {
            await MeasureQueryAsync(
                evidence,
                fixture.Session,
                fullRange,
                "full-range",
                query,
                cancellationToken);
        }

        await RunConcurrentQueryBatchAsync(
            evidence,
            fixture,
            fullRange,
            1,
            BenchmarkRandomSeed,
            cancellationToken);
        for (var snapshot = 1; snapshot <= SnapshotSequenceCount; snapshot++)
        {
            await CaptureSnapshotWithContinuityAsync(evidence, fixture, snapshot, cancellationToken);
            EnsureLatestSnapshotIsUsable(evidence, snapshot);
        }

        var finalRange = await CreateLatestRangeAsync(fixture, TimeSpan.FromSeconds(10), cancellationToken);
        var finalProfile = await MeasureQueryAsync(
            evidence,
            fixture.Session,
            finalRange,
            "post-snapshot-health",
            1,
            cancellationToken);
        var sessionDirectoryPresentBeforeEnd = Directory.Exists(fixture.SessionDirectory);
        await fixture.EndAsync(cancellationToken);
        RecordFinalSamplerState(evidence, fixture);
        var sessionDirectoryDeletedAfterEnd = !Directory.Exists(fixture.SessionDirectory);

        var throughputDropPercent = CalculateTargetThroughputDropPercent(evidence.TargetThroughput);
        var samplingResources = evidence.DiagnosticResources
            .Where(static measurement => measurement.Phase == "sampling")
            .ToArray();
        var processorDeltaSeconds = (
            samplingResources[^1].TotalProcessorMilliseconds
            - samplingResources[0].TotalProcessorMilliseconds) / 1_000d;
        var samplingWallSeconds = (
            samplingResources[^1].ObservedAtUtc
            - samplingResources[0].ObservedAtUtc).TotalSeconds;
        var averageCoreUsage = processorDeltaSeconds / samplingWallSeconds;
        var baselinePrivateMemory = samplingResources[0].PrivateMemoryBytes;
        var peakPrivateMemoryIncrease = Math.Max(
            0,
            samplingResources.Max(static measurement => measurement.PrivateMemoryBytes)
            - baselinePrivateMemory);
        var maximumStorageSize = evidence.StorageSizeBytes.Max();
        var measuredQueries = evidence.Queries
            .Where(static query => query.Kind == "full-range")
            .ToArray();
        var queryP95Milliseconds = Percentile95(
            measuredQueries.Select(static query => query.ElapsedMilliseconds).ToArray());
        var maximumQueryAllocation = measuredQueries.Max(static query => query.AllocatedBytes);
        var queryPrivateMemory = measuredQueries
            .Select(static query => query.PrivateMemoryAfterBytes)
            .ToArray();
        var queryPrivateMemoryLeak = HasMonotonicLeak(queryPrivateMemory);
        var concurrencyPassed = ConcurrentBatchesPassed(evidence.ConcurrentQueries);
        var snapshotsPassed = evidence.Snapshots.Count == SnapshotSequenceCount
            && evidence.Snapshots.All(ExecutionSamplingThresholds.IsUsableSnapshot);
        var allCountsConsistent = evidence.Queries.All(static query => query.CountsConsistent);
        var allLostEventsZero = evidence.LostEventCounts.All(static count => count == 0);

        AddThreshold(evidence, "targetThroughputMedianDropPercent", throughputDropPercent <= MaximumTargetThroughputDropPercent,
            "<=", throughputDropPercent, MaximumTargetThroughputDropPercent, "percent");
        AddThreshold(evidence, "diagnosticAverageCoreUsage", averageCoreUsage <= MaximumDiagnosticCoreUsage,
            "<=", averageCoreUsage, MaximumDiagnosticCoreUsage, "logical cores");
        AddThreshold(evidence, "diagnosticPeakPrivateMemoryIncrease", peakPrivateMemoryIncrease <= MaximumPrivateMemoryIncreaseBytes,
            "<=", peakPrivateMemoryIncrease, MaximumPrivateMemoryIncreaseBytes, "bytes");
        AddThreshold(evidence, "executionSessionStorage", maximumStorageSize <= MaximumStorageBytes,
            "<=", maximumStorageSize, MaximumStorageBytes, "bytes");
        AddThreshold(evidence, "fullRangeQueryP95", queryP95Milliseconds <= MaximumQueryP95Milliseconds,
            "<=", queryP95Milliseconds, MaximumQueryP95Milliseconds, "milliseconds");
        AddThreshold(evidence, "fullRangeQueryAllocation", maximumQueryAllocation <= MaximumQueryAllocationBytes,
            "<=", maximumQueryAllocation, MaximumQueryAllocationBytes, "bytes");
        AddThreshold(evidence, "queryPrivateMemoryNotMonotonic", !queryPrivateMemoryLeak,
            "==", queryPrivateMemoryLeak ? 1 : 0, 0, "boolean");
        AddThreshold(evidence, "lostEventCount", allLostEventsZero,
            "==", evidence.LostEventCounts.DefaultIfEmpty(-1).Max(), 0, "events");
        AddThreshold(evidence, "profileCountsConsistent", allCountsConsistent,
            "==", allCountsConsistent ? 1 : 0, 1, "boolean");
        AddThreshold(evidence, "concurrentQueriesAndCancellation", concurrencyPassed,
            "==", concurrencyPassed ? 1 : 0, 1, "boolean");
        AddThreshold(evidence, "snapshotSamplingContinuity", snapshotsPassed,
            "==", snapshotsPassed ? 1 : 0, 1, "boolean");
        AddThreshold(evidence, "samplingContinuedAfterSnapshots", finalProfile.ReceivedSampleCount > 0,
            ">", finalProfile.ReceivedSampleCount, 0, "samples");
        AddThreshold(evidence, "measurementDuration", samplingWallSeconds >= s_benchmarkMeasurementDuration.TotalSeconds,
            ">=", samplingWallSeconds, s_benchmarkMeasurementDuration.TotalSeconds, "seconds");
        AddThreshold(evidence, "sessionDirectoryPresentBeforeEnd", sessionDirectoryPresentBeforeEnd,
            "==", sessionDirectoryPresentBeforeEnd ? 1 : 0, 1, "boolean");
        AddThreshold(evidence, "sessionDirectoryDeletedAfterEnd", sessionDirectoryDeletedAfterEnd,
            "==", sessionDirectoryDeletedAfterEnd ? 1 : 0, 1, "boolean");
    }

    private static async Task RunStressAsync(
        ExecutionSamplingRunEvidence evidence,
        CancellationToken cancellationToken)
    {
        evidence.Configuration = new ExecutionSamplingConfigurationEvidence(
            WorkerCount,
            PathCount,
            SamplingConfiguration,
            s_warmupDuration.TotalSeconds,
            s_stressMeasurementDuration.TotalSeconds,
            s_throughputRoundDuration.TotalSeconds,
            s_resourceInterval.TotalSeconds,
            StressRandomSeed);

        await using var preparedTarget = await PreparedExecutionWorkloadTarget.CreateAsync(cancellationToken);
        await using var fixture = await AttachedExecutionFixture.StartAsync(preparedTarget, cancellationToken);
        evidence.Target = fixture.CreateTargetEvidence();
        await Task.Delay(s_warmupDuration, cancellationToken);

        var initialRange = await CreateLatestRangeAsync(fixture, TimeSpan.FromSeconds(10), cancellationToken);
        await MeasureQueryAsync(
            evidence,
            fixture.Session,
            initialRange,
            "stress-initial-health",
            0,
            cancellationToken);
        using var diagnosticsProcess = Process.GetCurrentProcess();
        var random = new Random(StressRandomSeed);
        var measurementStartedAtUtc = DateTimeOffset.UtcNow;
        var previousObservedAtUtc = measurementStartedAtUtc;
        var previousProcessorTime = ReadProcessorTime(diagnosticsProcess);
        var previousCompletedOperations = await fixture.Target.ReadCompletedOperationsAsync(cancellationToken);
        var firstResource = CaptureResourceMeasurement(
            "stress",
            diagnosticsProcess,
            fixture.SessionDirectory,
            measurementStartedAtUtc,
            measurementStartedAtUtc,
            previousProcessorTime,
            previousObservedAtUtc,
            previousCompletedOperations);
        evidence.DiagnosticResources.Add(firstResource);
        evidence.StorageSizeBytes.Add(firstResource.StorageBytes);
        var snapshotSequence = 0;

        for (var minute = 1; minute <= 120; minute++)
        {
            var scheduledAtUtc = measurementStartedAtUtc.AddTicks(s_resourceInterval.Ticks * minute);
            await DelayUntilAsync(scheduledAtUtc, cancellationToken);
            var cadenceLag = Math.Max(0, (DateTimeOffset.UtcNow - scheduledAtUtc).TotalSeconds);
            evidence.MinuteCadenceLagSeconds.Add(cadenceLag);

            var randomRange = CreateRandomLegalRange(fixture, random);
            await MeasureQueryAsync(
                evidence,
                fixture.Session,
                randomRange,
                "stress-minute-random",
                minute,
                cancellationToken);

            if (minute % 10 == 0)
            {
                var checkpointLag = Math.Max(0, (DateTimeOffset.UtcNow - scheduledAtUtc).TotalSeconds);
                evidence.TenMinuteCheckpointLagSeconds.Add(checkpointLag);

                var fullRange = new ExecutionTimeRange(
                    fixture.AttachedAtUtc.Add(s_availableRangeStartOffset),
                    DateTimeOffset.UtcNow.Subtract(s_availableRangeSafetyMargin));
                await RunConcurrentQueryBatchAsync(
                    evidence,
                    fixture,
                    fullRange,
                    minute / 10,
                    StressRandomSeed + minute,
                    cancellationToken);
                for (var snapshot = 0; snapshot < SnapshotSequenceCount; snapshot++)
                {
                    snapshotSequence++;
                    await CaptureSnapshotWithContinuityAsync(
                        evidence,
                        fixture,
                        snapshotSequence,
                        cancellationToken);
                    EnsureLatestSnapshotIsUsable(evidence, snapshotSequence);
                }
            }

            var completedOperations = await fixture.Target.ReadCompletedOperationsAsync(cancellationToken);
            var observedAtUtc = DateTimeOffset.UtcNow;
            var resource = CaptureResourceMeasurement(
                "stress",
                diagnosticsProcess,
                fixture.SessionDirectory,
                measurementStartedAtUtc,
                previousObservedAtUtc,
                previousProcessorTime,
                observedAtUtc,
                completedOperations);
            evidence.DiagnosticResources.Add(resource);
            evidence.StorageSizeBytes.Add(resource.StorageBytes);
            evidence.TargetThroughput.Add(new ExecutionSamplingThroughputMeasurement(
                "stress-minute",
                minute,
                fixture.Target.ProcessId,
                fixture.Target.StartTimeUtc,
                previousObservedAtUtc,
                resource.ObservedAtUtc,
                (resource.ObservedAtUtc - previousObservedAtUtc).TotalSeconds,
                previousCompletedOperations,
                completedOperations,
                completedOperations - previousCompletedOperations));
            previousObservedAtUtc = resource.ObservedAtUtc;
            previousProcessorTime = TimeSpan.FromMilliseconds(resource.TotalProcessorMilliseconds);
            previousCompletedOperations = completedOperations;
        }

        var fullStressRange = new ExecutionTimeRange(
            fixture.AttachedAtUtc.Add(s_availableRangeStartOffset),
            DateTimeOffset.UtcNow.Subtract(s_availableRangeSafetyMargin));
        var finalProfile = await MeasureQueryAsync(
            evidence,
            fixture.Session,
            fullStressRange,
            "stress-final-full-range",
            1,
            cancellationToken);
        var finalContinuityStartAtUtc = DateTimeOffset.UtcNow.Subtract(s_availableRangeSafetyMargin);
        await Task.Delay(s_finalContinuityObservationDuration, cancellationToken);
        var finalContinuityRange = new ExecutionTimeRange(
            finalContinuityStartAtUtc,
            DateTimeOffset.UtcNow.Subtract(s_availableRangeSafetyMargin));
        var finalContinuityProfile = await MeasureQueryAsync(
            evidence,
            fixture.Session,
            finalContinuityRange,
            "stress-final-fresh-continuity",
            1,
            cancellationToken);
        var sessionDirectoryPresentBeforeEnd = Directory.Exists(fixture.SessionDirectory);
        await fixture.EndAsync(cancellationToken);
        RecordFinalSamplerState(evidence, fixture);
        var sessionDirectoryDeletedAfterEnd = !Directory.Exists(fixture.SessionDirectory);

        var stressResources = evidence.DiagnosticResources
            .Where(static measurement => measurement.Phase == "stress")
            .ToArray();
        var privateMemory = stressResources
            .Select(static measurement => measurement.PrivateMemoryBytes)
            .ToArray();
        var handles = stressResources
            .Select(static measurement => (long)measurement.HandleCount)
            .ToArray();
        var stressWallSeconds = (
            stressResources[^1].ObservedAtUtc
            - stressResources[0].ObservedAtUtc).TotalSeconds;
        var randomQueryCount = evidence.Queries.Count(static query => query.Kind == "stress-minute-random");
        var allProfilesHaveSamples = evidence.ReceivedSampleCounts.All(static count => count > 0);
        var allCountsConsistent = evidence.Queries.All(static query => query.CountsConsistent);
        var allLostEventsZero = evidence.LostEventCounts.All(static count => count == 0);
        var concurrentQueriesPassed = evidence.ConcurrentQueries.Count == 12
            && ConcurrentBatchesPassed(evidence.ConcurrentQueries);
        var snapshotsPassed = evidence.Snapshots.Count == 12 * SnapshotSequenceCount
            && evidence.Snapshots.All(ExecutionSamplingThresholds.IsUsableSnapshot);
        var targetAlwaysProgressed = evidence.TargetThroughput.All(static throughput => throughput.CompletedOperations > 0);
        var memoryLeak = HasMonotonicLeak(privateMemory);
        var handleLeak = HasMonotonicLeak(handles);
        var samplingNeverInterrupted = allProfilesHaveSamples
            && finalProfile.ReceivedSampleCount > 0
            && finalContinuityProfile.ReceivedSampleCount > 0
            && CountsAreConsistent(finalContinuityProfile);

        AddThreshold(evidence, "stressDuration", stressWallSeconds >= s_stressMeasurementDuration.TotalSeconds,
            ">=", stressWallSeconds, s_stressMeasurementDuration.TotalSeconds, "seconds");
        AddThreshold(evidence, "minuteRandomQueryCount", randomQueryCount == 120,
            "==", randomQueryCount, 120, "queries");
        AddThreshold(evidence, "tenMinuteConcurrentQueryBatches", concurrentQueriesPassed,
            "==", concurrentQueriesPassed ? 1 : 0, 1, "boolean");
        AddThreshold(evidence, "tenMinuteTripleSnapshots", snapshotsPassed,
            "==", snapshotsPassed ? 1 : 0, 1, "boolean");
        AddThreshold(evidence, "samplingNeverInterrupted", samplingNeverInterrupted,
            "==", samplingNeverInterrupted ? 1 : 0, 1, "boolean");
        AddThreshold(evidence, "lostEventCount", allLostEventsZero,
            "==", evidence.LostEventCounts.DefaultIfEmpty(-1).Max(), 0, "events");
        AddThreshold(evidence, "profileCountsConsistent", allCountsConsistent,
            "==", allCountsConsistent ? 1 : 0, 1, "boolean");
        AddThreshold(evidence, "targetWorkloadProgress", targetAlwaysProgressed,
            "==", targetAlwaysProgressed ? 1 : 0, 1, "boolean");
        AddThreshold(evidence, "diagnosticPrivateMemoryNotMonotonic", !memoryLeak,
            "==", memoryLeak ? 1 : 0, 0, "boolean");
        AddThreshold(evidence, "diagnosticHandleCountNotMonotonic", !handleLeak,
            "==", handleLeak ? 1 : 0, 0, "boolean");
        AddThreshold(evidence, "sessionDirectoryPresentBeforeEnd", sessionDirectoryPresentBeforeEnd,
            "==", sessionDirectoryPresentBeforeEnd ? 1 : 0, 1, "boolean");
        AddThreshold(evidence, "sessionDirectoryDeletedAfterEnd", sessionDirectoryDeletedAfterEnd,
            "==", sessionDirectoryDeletedAfterEnd ? 1 : 0, 1, "boolean");
    }

    private static async Task<ExecutionSamplingThroughputMeasurement> MeasureUnattachedThroughputAsync(
        PreparedExecutionWorkloadTarget preparedTarget,
        int sequence,
        CancellationToken cancellationToken)
    {
        await using var target = await preparedTarget.StartAsync(cancellationToken);
        await Task.Delay(s_warmupDuration, cancellationToken);
        var startingCount = await target.ReadCompletedOperationsAsync(cancellationToken);
        var startedAtUtc = DateTimeOffset.UtcNow;
        await Task.Delay(s_throughputRoundDuration, cancellationToken);
        var endingCount = await target.ReadCompletedOperationsAsync(cancellationToken);
        var completedAtUtc = DateTimeOffset.UtcNow;
        return new ExecutionSamplingThroughputMeasurement(
            "unattached",
            sequence,
            target.ProcessId,
            target.StartTimeUtc,
            startedAtUtc,
            completedAtUtc,
            (completedAtUtc - startedAtUtc).TotalSeconds,
            startingCount,
            endingCount,
            endingCount - startingCount);
    }

    private static Task<ExecutionSamplingAttributionMeasurement> MeasureAttributionAsync(
        ExecutionSamplingAttributionMode mode,
        PreparedExecutionWorkloadTarget preparedTarget,
        int round,
        int orderPosition,
        CancellationToken cancellationToken) =>
        mode switch
        {
            ExecutionSamplingAttributionMode.Unattached => MeasureUnattachedAttributionAsync(
                preparedTarget,
                round,
                orderPosition,
                cancellationToken),
            ExecutionSamplingAttributionMode.ExecutionOnly => MeasureExecutionOnlyAttributionAsync(
                preparedTarget,
                round,
                orderPosition,
                cancellationToken),
            ExecutionSamplingAttributionMode.FullAttach => MeasureFullAttachAttributionAsync(
                preparedTarget,
                round,
                orderPosition,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown attribution mode.")
        };

    private static async Task<ExecutionSamplingAttributionMeasurement> MeasureUnattachedAttributionAsync(
        PreparedExecutionWorkloadTarget preparedTarget,
        int round,
        int orderPosition,
        CancellationToken cancellationToken)
    {
        await using var target = await preparedTarget.StartAsync(cancellationToken);
        return await MeasureAttributionWindowAsync(
            "unattached",
            round,
            orderPosition,
            target,
            sessionDirectory: null,
            sampler: null,
            cancellationToken);
    }

    private static async Task<ExecutionSamplingAttributionMeasurement> MeasureExecutionOnlyAttributionAsync(
        PreparedExecutionWorkloadTarget preparedTarget,
        int round,
        int orderPosition,
        CancellationToken cancellationToken)
    {
        await using var target = await preparedTarget.StartAsync(cancellationToken);
        var root = Path.Combine(
            Path.GetTempPath(),
            "DotnetAnalysis.Diagnostics.IntegrationTests",
            "ExecutionAttribution",
            Guid.NewGuid().ToString("N"));
        var layout = new ExecutionCaptureStorageLayout(root);
        var store = new ExecutionCaptureStore(layout);
        var sampler = new EventPipeExecutionSampler(store);
        var session = new ExecutionSamplingSession(store, sampler, new ExecutionSymbolResolver());
        try
        {
            var targetProcess = new TargetProcess(
                target.ProcessId,
                target.StartTimeUtc,
                Path.GetFileNameWithoutExtension(target.ExecutablePath),
                target.ExecutablePath);
            await session.StartAsync(targetProcess, cancellationToken);
            var queryableRangeStartedAtUtc = DateTimeOffset.UtcNow;
            var measurement = await MeasureAttributionWindowAsync(
                "execution-only",
                round,
                orderPosition,
                target,
                layout.SessionDirectory,
                sampler,
                cancellationToken);
            await sampler.StopAsync(cancellationToken);
            var captureDurationSeconds = (DateTimeOffset.UtcNow - queryableRangeStartedAtUtc).TotalSeconds;
            var query = await MeasureCachedFullRangeQueriesAsync(
                session.GetExecutionProfileAsync,
                store,
                queryableRangeStartedAtUtc,
                cancellationToken);
            return measurement with
            {
                CaptureDurationSeconds = captureDurationSeconds,
                StorageBytes = GetDirectorySize(layout.SessionDirectory),
                SuccessfulSampleCount = sampler.SuccessfulSampleCount,
                LostEventCount = sampler.LostEventCount,
                CachedFullRangeSampleCount = query.SampleCount,
                CachedFullRangeQueryMilliseconds = query.ElapsedMilliseconds,
                CachedFullRangeQueryAllocatedBytes = query.AllocatedBytes,
                CachedFullRangeQueryP95Milliseconds = query.P95Milliseconds,
                CachedFullRangeQueryMaximumAllocatedBytes = query.MaximumAllocatedBytes
            };
        }
        finally
        {
            await session.DisposeAsync().AsTask()
                .WaitAsync(s_cleanupStepTimeout, CancellationToken.None);
            await DeleteDirectoryWithRetriesAsync(root);
        }
    }

    private static async Task<ExecutionSamplingAttributionMeasurement> MeasureProviderOnlyAttributionAsync(
        PreparedExecutionWorkloadTarget preparedTarget,
        int round,
        int orderPosition,
        CancellationToken cancellationToken)
    {
        await using var target = await preparedTarget.StartAsync(cancellationToken);
        var client = new DiagnosticsClient(target.ProcessId);
        using var session = client.StartEventPipeSession(
            EventPipeExecutionSampler.CreateDefaultProviders(),
            EventPipeExecutionSampler.DefaultRequestRundown,
            EventPipeExecutionSampler.DefaultCircularBufferMegabytes);
        try
        {
            return await MeasureAttributionWindowAsync(
                "provider-only",
                round,
                orderPosition,
                target,
                sessionDirectory: null,
                sampler: null,
                cancellationToken);
        }
        finally
        {
            var drainTask = session.EventStream.CopyToAsync(Stream.Null, CancellationToken.None);
            session.Stop();
            await drainTask.WaitAsync(s_cleanupStepTimeout, CancellationToken.None);
        }
    }

    private static async Task<ExecutionSamplingAttributionMeasurement> MeasureFullAttachAttributionAsync(
        PreparedExecutionWorkloadTarget preparedTarget,
        int round,
        int orderPosition,
        CancellationToken cancellationToken)
    {
        await using var fixture = await AttachedExecutionFixture.StartAsync(preparedTarget, cancellationToken);
        var measurement = await MeasureAttributionWindowAsync(
            "full-attach",
            round,
            orderPosition,
            fixture.Target,
            fixture.SessionDirectory,
            fixture.ExecutionSampler,
            cancellationToken);
        await fixture.ExecutionSampler.StopAsync(cancellationToken);
        var captureDurationSeconds = (DateTimeOffset.UtcNow - fixture.AttachedAtUtc).TotalSeconds;
        var query = await MeasureCachedFullRangeQueriesAsync(
            fixture.Session.GetExecutionProfileAsync,
            fixture.ExecutionStore,
            fixture.AttachedAtUtc,
            cancellationToken);
        measurement = measurement with
        {
            CaptureDurationSeconds = captureDurationSeconds,
            StorageBytes = GetDirectorySize(fixture.SessionDirectory),
            SuccessfulSampleCount = fixture.ExecutionSampler.SuccessfulSampleCount,
            LostEventCount = fixture.ExecutionSampler.LostEventCount,
            CachedFullRangeSampleCount = query.SampleCount,
            CachedFullRangeQueryMilliseconds = query.ElapsedMilliseconds,
            CachedFullRangeQueryAllocatedBytes = query.AllocatedBytes,
            CachedFullRangeQueryP95Milliseconds = query.P95Milliseconds,
            CachedFullRangeQueryMaximumAllocatedBytes = query.MaximumAllocatedBytes
        };
        await fixture.EndAsync(cancellationToken);
        return measurement;
    }

    private static async Task<ExecutionSamplingAttributionMeasurement> MeasureAttributionWindowAsync(
        string mode,
        int round,
        int orderPosition,
        ControlledExecutionWorkloadTarget target,
        string? sessionDirectory,
        EventPipeExecutionSampler? sampler,
        CancellationToken cancellationToken)
    {
        await Task.Delay(s_attributionWarmupDuration, cancellationToken);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var diagnosticsProcess = Process.GetCurrentProcess();
        diagnosticsProcess.Refresh();
        var processorBefore = diagnosticsProcess.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var completedBefore = await target.ReadCompletedOperationsAsync(cancellationToken);
        var startedAtUtc = DateTimeOffset.UtcNow;
        await Task.Delay(s_attributionMeasurementDuration, cancellationToken);
        var completedAfter = await target.ReadCompletedOperationsAsync(cancellationToken);
        var completedAtUtc = DateTimeOffset.UtcNow;
        diagnosticsProcess.Refresh();
        var processorAfter = diagnosticsProcess.TotalProcessorTime;
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        var durationSeconds = (completedAtUtc - startedAtUtc).TotalSeconds;
        return new ExecutionSamplingAttributionMeasurement(
            mode,
            round,
            orderPosition,
            GetAttributionOrderTemperature(orderPosition),
            startedAtUtc,
            completedAtUtc,
            durationSeconds,
            completedAfter - completedBefore,
            (completedAfter - completedBefore) / durationSeconds,
            (processorAfter - processorBefore).TotalSeconds / durationSeconds,
            Math.Max(0, allocatedAfter - allocatedBefore),
            sessionDirectory is null ? 0 : GetDirectorySize(sessionDirectory),
            sampler?.SuccessfulSampleCount ?? 0,
            sampler?.LostEventCount ?? 0,
            CaptureDurationSeconds: 0,
            CachedFullRangeSampleCount: 0,
            CachedFullRangeQueryMilliseconds: [],
            CachedFullRangeQueryAllocatedBytes: [],
            CachedFullRangeQueryP95Milliseconds: null,
            CachedFullRangeQueryMaximumAllocatedBytes: null);
    }

    private static async Task<ExecutionSamplingAttributionQueryMeasurement> MeasureCachedFullRangeQueriesAsync(
        Func<ExecutionTimeRange, CancellationToken, Task<ExecutionProfile>> queryAsync,
        ExecutionCaptureStore store,
        DateTimeOffset queryableRangeStartedAtUtc,
        CancellationToken cancellationToken)
    {
        var boundary = store.CaptureReadBoundary();
        if (boundary.SegmentReadLimits.Count == 0)
        {
            throw new InvalidDataException("The attribution capture did not produce a readable sample range.");
        }

        var firstSampleAtUtc = boundary.SegmentReadLimits.Min(static segment => segment.StartedAtUtc);
        var range = new ExecutionTimeRange(
            firstSampleAtUtc > queryableRangeStartedAtUtc ? firstSampleAtUtc : queryableRangeStartedAtUtc,
            boundary.WrittenThroughUtc);
        var warmProfile = await queryAsync(range, cancellationToken);
        if (warmProfile.ReceivedSampleCount == 0 || !CountsAreConsistent(warmProfile))
        {
            throw new InvalidDataException("The attribution warm query did not produce a consistent profile.");
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var elapsedMilliseconds = new double[AttributionQueryCount];
        var allocatedBytes = new long[AttributionQueryCount];
        for (var queryIndex = 0; queryIndex < AttributionQueryCount; queryIndex++)
        {
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();
            var profile = await queryAsync(range, cancellationToken);
            stopwatch.Stop();
            var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
            if (profile.ReceivedSampleCount != warmProfile.ReceivedSampleCount || !CountsAreConsistent(profile))
            {
                throw new InvalidDataException("A cached attribution query returned inconsistent profile counts.");
            }

            elapsedMilliseconds[queryIndex] = stopwatch.Elapsed.TotalMilliseconds;
            allocatedBytes[queryIndex] = Math.Max(0, allocatedAfter - allocatedBefore);
        }

        return new ExecutionSamplingAttributionQueryMeasurement(
            warmProfile.ReceivedSampleCount,
            elapsedMilliseconds,
            allocatedBytes,
            Percentile95((double[])elapsedMilliseconds.Clone()),
            allocatedBytes.Max());
    }

    private static string GetAttributionOrderTemperature(int orderPosition) =>
        orderPosition switch
        {
            1 => "cold",
            2 => "middle",
            3 => "warm",
            _ => throw new ArgumentOutOfRangeException(nameof(orderPosition), orderPosition, "Order position must be between one and three.")
        };

    private static ExecutionSamplingAttributionSummary CreateAttributionSummary(
        IReadOnlyCollection<ExecutionSamplingAttributionMeasurement> measurements)
    {
        var baselinesByRound = measurements
            .Where(static measurement => measurement.Mode == "unattached")
            .ToDictionary(static measurement => measurement.Round);
        var executionOnlyMeasurements = measurements
            .Where(static measurement => measurement.Mode == "execution-only")
            .ToArray();
        var fullAttachMeasurements = measurements
            .Where(static measurement => measurement.Mode == "full-attach")
            .ToArray();
        var executionOnlyDrops = CalculatePairedAttributionDrops(executionOnlyMeasurements, baselinesByRound);
        var fullAttachDrops = CalculatePairedAttributionDrops(fullAttachMeasurements, baselinesByRound);
        var sampledMeasurements = executionOnlyMeasurements.Concat(fullAttachMeasurements).ToArray();
        var queryDurations = sampledMeasurements
            .SelectMany(static measurement => measurement.CachedFullRangeQueryMilliseconds)
            .ToArray();
        var queryAllocations = sampledMeasurements
            .SelectMany(static measurement => measurement.CachedFullRangeQueryAllocatedBytes)
            .ToArray();
        var projectedStorageBytes = sampledMeasurements
            .Select(static measurement => (long)Math.Ceiling(
                measurement.StorageBytes * 3_600d / measurement.CaptureDurationSeconds))
            .ToArray();

        return new ExecutionSamplingAttributionSummary(
            executionOnlyDrops,
            fullAttachDrops,
            Median((double[])executionOnlyDrops.Clone()),
            Median((double[])fullAttachDrops.Clone()),
            Median(executionOnlyMeasurements
                .Select(static measurement => measurement.DiagnosticAverageCoreUsage)
                .ToArray()),
            Median(fullAttachMeasurements
                .Select(static measurement => measurement.DiagnosticAverageCoreUsage)
                .ToArray()),
            Percentile95(queryDurations),
            queryAllocations.Max(),
            sampledMeasurements.Max(static measurement => measurement.StorageBytes),
            projectedStorageBytes.Max());
    }

    private static double[] CalculatePairedAttributionDrops(
        IReadOnlyCollection<ExecutionSamplingAttributionMeasurement> attachedMeasurements,
        IReadOnlyDictionary<int, ExecutionSamplingAttributionMeasurement> baselinesByRound) =>
        attachedMeasurements
            .OrderBy(static measurement => measurement.Round)
            .Select(measurement => ExecutionSamplingThresholds.GetTargetThroughputDropPercent(
                baselinesByRound[measurement.Round].TargetOperationsPerSecond,
                measurement.TargetOperationsPerSecond))
            .ToArray();

    private static async Task<ExecutionSamplingThroughputMeasurement> MeasureAttachedThroughputAsync(
        PreparedExecutionWorkloadTarget preparedTarget,
        int sequence,
        ExecutionSamplingRunEvidence evidence,
        CancellationToken cancellationToken)
    {
        await using var fixture = await AttachedExecutionFixture.StartAsync(preparedTarget, cancellationToken);
        await Task.Delay(s_warmupDuration, cancellationToken);
        var startingCount = await fixture.Target.ReadCompletedOperationsAsync(cancellationToken);
        var startedAtUtc = DateTimeOffset.UtcNow;
        await Task.Delay(s_throughputRoundDuration, cancellationToken);
        var endingCount = await fixture.Target.ReadCompletedOperationsAsync(cancellationToken);
        var completedAtUtc = DateTimeOffset.UtcNow;
        var range = new ExecutionTimeRange(
            fixture.AttachedAtUtc.Add(s_availableRangeStartOffset),
            completedAtUtc.Subtract(s_availableRangeSafetyMargin));
        var profile = await fixture.Session.GetExecutionProfileAsync(range, cancellationToken);
        RecordProfile(evidence, profile);
        if (!CountsAreConsistent(profile) || profile.ReceivedSampleCount == 0 || profile.LostEventCount != 0)
        {
            throw new InvalidDataException(
                $"Attached throughput round {sequence} produced invalid execution sampling data.");
        }

        await fixture.EndAsync(cancellationToken);
        RecordFinalSamplerState(evidence, fixture);
        if (Directory.Exists(fixture.SessionDirectory))
        {
            throw new IOException($"Execution session directory was not removed: {fixture.SessionDirectory}");
        }

        return new ExecutionSamplingThroughputMeasurement(
            "attached",
            sequence,
            fixture.Target.ProcessId,
            fixture.Target.StartTimeUtc,
            startedAtUtc,
            completedAtUtc,
            (completedAtUtc - startedAtUtc).TotalSeconds,
            startingCount,
            endingCount,
            endingCount - startingCount);
    }

    private static async Task<ExecutionProfile> MeasureQueryAsync(
        ExecutionSamplingRunEvidence evidence,
        IProcessDiagnosticsSession session,
        ExecutionTimeRange range,
        string kind,
        int sequence,
        CancellationToken cancellationToken)
    {
        using var diagnosticsProcess = Process.GetCurrentProcess();
        diagnosticsProcess.Refresh();
        var privateMemoryBefore = diagnosticsProcess.PrivateMemorySize64;
        var processorBefore = diagnosticsProcess.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var startedAtUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var profile = await session.GetExecutionProfileAsync(range, cancellationToken);
        stopwatch.Stop();
        var completedAtUtc = DateTimeOffset.UtcNow;
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        diagnosticsProcess.Refresh();
        var privateMemoryAfter = diagnosticsProcess.PrivateMemorySize64;
        var processorAfter = diagnosticsProcess.TotalProcessorTime;
        var hotspotExclusiveCount = profile.Hotspots.Sum(static hotspot => hotspot.ExclusiveSampleCount);
        var callTreeRootInclusiveCount = profile.CallTreeRoots.Sum(static root => root.InclusiveSampleCount);
        var countsConsistent = CountsAreConsistent(profile);
        evidence.Queries.Add(new ExecutionSamplingQueryMeasurement(
            kind,
            sequence,
            startedAtUtc,
            completedAtUtc,
            range.StartAtUtc,
            range.EndAtUtc,
            stopwatch.Elapsed.TotalMilliseconds,
            Math.Max(0, allocatedAfter - allocatedBefore),
            (processorAfter - processorBefore).TotalMilliseconds,
            privateMemoryBefore,
            privateMemoryAfter,
            profile.ReceivedSampleCount,
            profile.LostEventCount,
            hotspotExclusiveCount,
            callTreeRootInclusiveCount,
            countsConsistent));
        RecordProfile(evidence, profile);
        return profile;
    }

    private static async Task RunConcurrentQueryBatchAsync(
        ExecutionSamplingRunEvidence evidence,
        AttachedExecutionFixture fixture,
        ExecutionTimeRange availableRange,
        int sequence,
        int randomSeed,
        CancellationToken cancellationToken)
    {
        var ranges = CreateConcurrentRanges(availableRange, randomSeed);
        using var firstCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var secondCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        fixture.ConcurrentQueryEntryGate.Arm(MaximumConcurrentIncrementalProfileBuilds);
        var startedAtUtc = DateTimeOffset.UtcNow;
        var tasks = ranges
            .Select((range, index) => fixture.Session.GetExecutionProfileAsync(
                range,
                index switch
                {
                    0 => firstCancellation.Token,
                    1 => secondCancellation.Token,
                    _ => cancellationToken
                }))
            .ToArray();
        var outcomes = new List<ExecutionSamplingConcurrentQueryOutcome>(ConcurrentQueryCount);
        try
        {
            await fixture.ConcurrentQueryEntryGate.WaitForAllQueriesEnteredAsync(
                s_cleanupStepTimeout,
                cancellationToken);
            var enteredBuildCount = fixture.ConcurrentQueryEntryGate.EnteredQueryCount;
            if (enteredBuildCount != MaximumConcurrentIncrementalProfileBuilds)
            {
                throw new InvalidDataException(
                    $"Concurrent query batch {sequence} entered {enteredBuildCount} actual builds before release; "
                    + $"the limit is {MaximumConcurrentIncrementalProfileBuilds}.");
            }

            var enteredQueryCount = tasks.Length;
            firstCancellation.Cancel();
            secondCancellation.Cancel();
            fixture.ConcurrentQueryEntryGate.Release();
            for (var index = 0; index < tasks.Length; index++)
            {
                try
                {
                    var profile = await tasks[index];
                    RecordProfile(evidence, profile);
                    outcomes.Add(new ExecutionSamplingConcurrentQueryOutcome(
                        index,
                        ranges[index].StartAtUtc,
                        ranges[index].EndAtUtc,
                        index < ConcurrentCancellationCount,
                        "succeeded",
                        profile.ReceivedSampleCount,
                        profile.LostEventCount,
                        CountsAreConsistent(profile),
                        null));
                }
                catch (OperationCanceledException exception) when (
                    index < ConcurrentCancellationCount
                    && (firstCancellation.IsCancellationRequested || secondCancellation.IsCancellationRequested))
                {
                    outcomes.Add(new ExecutionSamplingConcurrentQueryOutcome(
                        index,
                        ranges[index].StartAtUtc,
                        ranges[index].EndAtUtc,
                        true,
                        "cancelled",
                        null,
                        null,
                        null,
                        exception.ToString()));
                }
                catch (Exception exception)
                {
                    outcomes.Add(new ExecutionSamplingConcurrentQueryOutcome(
                        index,
                        ranges[index].StartAtUtc,
                        ranges[index].EndAtUtc,
                        index < ConcurrentCancellationCount,
                        "failed",
                        null,
                        null,
                        null,
                        exception.ToString()));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var batch = new ExecutionSamplingConcurrentQueryBatch(
                sequence,
                startedAtUtc,
                DateTimeOffset.UtcNow,
                enteredQueryCount,
                outcomes);
            evidence.ConcurrentQueries.Add(batch);
            if (!ConcurrentBatchesPassed([batch]))
            {
                throw new InvalidDataException(
                    $"Concurrent query batch {sequence} did not start all eight query calls with at most two "
                    + "gated actual builds before producing exactly six consistent successes and two cancellations.");
            }
        }
        finally
        {
            fixture.ConcurrentQueryEntryGate.Release();
            await ((Task)Task.WhenAll(tasks)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            fixture.ConcurrentQueryEntryGate.Disarm();
        }
    }

    private static async Task CaptureSnapshotWithContinuityAsync(
        ExecutionSamplingRunEvidence evidence,
        AttachedExecutionFixture fixture,
        int sequence,
        CancellationToken cancellationToken)
    {
        var beforeRange = await CreateLatestRangeAsync(fixture, TimeSpan.FromSeconds(5), cancellationToken);
        var beforeProfile = await fixture.Session.GetExecutionProfileAsync(beforeRange, cancellationToken);
        RecordProfile(evidence, beforeProfile);
        using var diagnosticsProcess = Process.GetCurrentProcess();
        diagnosticsProcess.Refresh();
        var privateMemoryBefore = diagnosticsProcess.PrivateMemorySize64;
        var processorBefore = diagnosticsProcess.TotalProcessorTime;
        var startedAtUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var snapshot = await fixture.Session.CaptureSnapshotAsync(cancellationToken);
            stopwatch.Stop();
            await Task.Delay(s_snapshotRecoveryDelay, cancellationToken);
            var afterRange = new ExecutionTimeRange(
                beforeRange.EndAtUtc,
                DateTimeOffset.UtcNow.Subtract(s_availableRangeSafetyMargin));
            var afterProfile = await fixture.Session.GetExecutionProfileAsync(afterRange, cancellationToken);
            RecordProfile(evidence, afterProfile);
            diagnosticsProcess.Refresh();
            var privateMemoryAfter = diagnosticsProcess.PrivateMemorySize64;
            var processorAfter = diagnosticsProcess.TotalProcessorTime;
            var dumpPath = fixture.SnapshotLayout.GetFinalDumpPath(snapshot.Id);
            var fileSize = File.Exists(dumpPath) ? (long?)new FileInfo(dumpPath).Length : null;
            evidence.Snapshots.Add(new ExecutionSamplingSnapshotMeasurement(
                sequence,
                startedAtUtc,
                DateTimeOffset.UtcNow,
                stopwatch.Elapsed.TotalMilliseconds,
                snapshot.Id.ToString(),
                snapshot.State.ToString(),
                fileSize,
                (processorAfter - processorBefore).TotalMilliseconds,
                privateMemoryBefore,
                privateMemoryAfter,
                beforeProfile.ReceivedSampleCount,
                afterProfile.ReceivedSampleCount,
                Math.Max(beforeProfile.LostEventCount, afterProfile.LostEventCount),
                beforeProfile.ReceivedSampleCount > 0
                    && afterProfile.ReceivedSampleCount > 0
                    && CountsAreConsistent(beforeProfile)
                    && CountsAreConsistent(afterProfile),
                null));
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            diagnosticsProcess.Refresh();
            evidence.Snapshots.Add(new ExecutionSamplingSnapshotMeasurement(
                sequence,
                startedAtUtc,
                DateTimeOffset.UtcNow,
                stopwatch.Elapsed.TotalMilliseconds,
                null,
                null,
                null,
                (diagnosticsProcess.TotalProcessorTime - processorBefore).TotalMilliseconds,
                privateMemoryBefore,
                diagnosticsProcess.PrivateMemorySize64,
                beforeProfile.ReceivedSampleCount,
                0,
                beforeProfile.LostEventCount,
                false,
                exception.ToString()));
            evidence.Exceptions.Add(ExecutionSamplingExceptionEvidence.From($"snapshot-{sequence}", exception));
            throw;
        }
    }

    private static ExecutionSamplingResourceMeasurement CaptureResourceMeasurement(
        string phase,
        Process diagnosticsProcess,
        string sessionDirectory,
        DateTimeOffset measurementStartedAtUtc,
        DateTimeOffset previousObservedAtUtc,
        TimeSpan previousProcessorTime,
        DateTimeOffset observedAtUtc,
        long targetCompletedOperations)
    {
        diagnosticsProcess.Refresh();
        var processorTime = diagnosticsProcess.TotalProcessorTime;
        var intervalSeconds = (observedAtUtc - previousObservedAtUtc).TotalSeconds;
        var intervalCoreUsage = intervalSeconds <= 0
            ? 0
            : (processorTime - previousProcessorTime).TotalSeconds / intervalSeconds;
        return new ExecutionSamplingResourceMeasurement(
            phase,
            observedAtUtc,
            (observedAtUtc - measurementStartedAtUtc).TotalSeconds,
            processorTime.TotalMilliseconds,
            intervalCoreUsage,
            diagnosticsProcess.PrivateMemorySize64,
            diagnosticsProcess.HandleCount,
            GetDirectorySize(sessionDirectory),
            targetCompletedOperations);
    }

    private static TimeSpan ReadProcessorTime(Process process)
    {
        process.Refresh();
        return process.TotalProcessorTime;
    }

    private static void RecordProfile(ExecutionSamplingRunEvidence evidence, ExecutionProfile profile)
    {
        evidence.ReceivedSampleCounts.Add(profile.ReceivedSampleCount);
        evidence.LostEventCounts.Add(profile.LostEventCount);
    }

    private static void RecordFinalSamplerState(
        ExecutionSamplingRunEvidence evidence,
        AttachedExecutionFixture fixture)
    {
        evidence.ReceivedSampleCounts.Add(fixture.FinalSuccessfulSampleCount);
        evidence.LostEventCounts.Add(fixture.FinalLostEventCount);
    }

    private static void EnsureLatestSnapshotIsUsable(
        ExecutionSamplingRunEvidence evidence,
        int sequence)
    {
        var snapshot = evidence.Snapshots[^1];
        if (!ExecutionSamplingThresholds.IsUsableSnapshot(snapshot))
        {
            throw new InvalidDataException(
                $"Snapshot sequence {sequence} did not produce a non-empty gcdump while execution sampling continued.");
        }
    }

    private static bool CountsAreConsistent(ExecutionProfile profile) =>
        profile.ReceivedSampleCount > 0
        && profile.ReceivedSampleCount == profile.Hotspots.Sum(static hotspot => hotspot.ExclusiveSampleCount)
        && profile.ReceivedSampleCount == profile.CallTreeRoots.Sum(static root => root.InclusiveSampleCount);

    private static ExecutionTimeRange[] CreateConcurrentRanges(
        ExecutionTimeRange availableRange,
        int randomSeed)
    {
        var random = new Random(randomSeed);
        var availableTicks = availableRange.EndAtUtc.Ticks - availableRange.StartAtUtc.Ticks;
        var latestStartOffset = availableTicks / 5;
        var earliestEndOffset = availableTicks * 4 / 5;
        var ranges = new ExecutionTimeRange[ConcurrentQueryCount];
        for (var index = 0; index < ranges.Length; index++)
        {
            ranges[index] = new ExecutionTimeRange(
                availableRange.StartAtUtc.AddTicks(random.NextInt64(latestStartOffset + 1)),
                availableRange.StartAtUtc.AddTicks(random.NextInt64(earliestEndOffset, availableTicks + 1)));
        }

        return ranges;
    }

    private static ExecutionTimeRange CreateRandomLegalRange(
        AttachedExecutionFixture fixture,
        Random random)
    {
        var availableStart = fixture.AttachedAtUtc.Add(s_availableRangeStartOffset);
        var availableEnd = DateTimeOffset.UtcNow.Subtract(s_availableRangeSafetyMargin);
        var maximumDurationTicks = Math.Min(
            TimeSpan.FromMinutes(1).Ticks,
            availableEnd.Ticks - availableStart.Ticks);
        var minimumDurationTicks = Math.Min(TimeSpan.FromSeconds(5).Ticks, maximumDurationTicks);
        var durationTicks = random.NextInt64(minimumDurationTicks, maximumDurationTicks + 1);
        var latestStartTicks = availableEnd.Ticks - durationTicks;
        var startTicks = random.NextInt64(availableStart.Ticks, latestStartTicks + 1);
        return new ExecutionTimeRange(
            new DateTimeOffset(startTicks, TimeSpan.Zero),
            new DateTimeOffset(startTicks + durationTicks, TimeSpan.Zero));
    }

    private static async Task<ExecutionTimeRange> CreateLatestRangeAsync(
        AttachedExecutionFixture fixture,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var minimumEnd = fixture.AttachedAtUtc.Add(s_availableRangeStartOffset).Add(duration);
        await DelayUntilAsync(minimumEnd.Add(s_availableRangeSafetyMargin), cancellationToken);
        var endAtUtc = DateTimeOffset.UtcNow.Subtract(s_availableRangeSafetyMargin);
        var earliestStart = fixture.AttachedAtUtc.Add(s_availableRangeStartOffset);
        var startAtUtc = endAtUtc.Subtract(duration);
        if (startAtUtc < earliestStart)
        {
            startAtUtc = earliestStart;
        }

        return new ExecutionTimeRange(startAtUtc, endAtUtc);
    }

    private static async Task DelayUntilAsync(DateTimeOffset targetAtUtc, CancellationToken cancellationToken)
    {
        var delay = targetAtUtc - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static long GetDirectorySize(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                total = checked(total + new FileInfo(path).Length);
            }
            catch (FileNotFoundException)
            {
            }
        }

        return total;
    }

    private static double GetOperationsPerSecond(ExecutionSamplingThroughputMeasurement measurement) =>
        measurement.DurationSeconds <= 0
            ? 0
            : measurement.CompletedOperations / measurement.DurationSeconds;

    private static ExecutionSamplingRunEvidence CreateSuccessfulEvidenceWithRawMeasurements(
        DateTimeOffset startedAtUtc,
        string runKind = "benchmark")
    {
        var evidence = ExecutionSamplingRunEvidence.Create(runKind);
        evidence.ReceivedSampleCounts.Add(1);
        evidence.LostEventCounts.Add(0);
        evidence.TargetThroughput.Add(new ExecutionSamplingThroughputMeasurement(
            "attached",
            1,
            123,
            startedAtUtc,
            startedAtUtc,
            startedAtUtc.AddSeconds(1),
            1,
            0,
            1,
            1));
        evidence.DiagnosticResources.Add(new ExecutionSamplingResourceMeasurement(
            "sampling",
            startedAtUtc,
            1,
            1,
            0.01,
            1,
            1,
            1,
            1));
        evidence.StorageSizeBytes.Add(1);
        evidence.Queries.Add(new ExecutionSamplingQueryMeasurement(
            "full-range",
            1,
            startedAtUtc,
            startedAtUtc.AddSeconds(1),
            startedAtUtc,
            startedAtUtc.AddSeconds(1),
            1,
            1,
            1,
            1,
            1,
            1,
            0,
            1,
            1,
            true));
        evidence.ConcurrentQueries.Add(new ExecutionSamplingConcurrentQueryBatch(
            1,
            startedAtUtc,
            startedAtUtc.AddSeconds(1),
            ConcurrentQueryCount,
            []));
        evidence.Snapshots.Add(new ExecutionSamplingSnapshotMeasurement(
            1,
            startedAtUtc,
            startedAtUtc.AddSeconds(1),
            1,
            Guid.Empty.ToString("N"),
            MemorySnapshotState.Analyzing.ToString(),
            1,
            1,
            1,
            1,
            1,
            1,
            0,
            true,
            null));
        evidence.Thresholds.Add(
            "sampleThreshold",
            new ExecutionSamplingThresholdDecision(true, "==", 1, 1, "count"));
        return evidence;
    }

    private static void PopulateTraceabilityContext(
        ExecutionSamplingRunEvidence evidence,
        DateTimeOffset startedAtUtc)
    {
        evidence.CompletedAtUtc = startedAtUtc.AddSeconds(1);
        evidence.Environment = new ExecutionSamplingEnvironmentEvidence(
            "0123456789abcdef",
            false,
            "10.0.100",
            Environment.Version.ToString(),
            "Windows",
            "X64",
            "X64",
            "test processor",
            8);
        evidence.Target = new ExecutionSamplingTargetEvidence(
            "C:\\test\\target.exe",
            "C:\\test\\workload.dll",
            TargetFramework,
            123,
            startedAtUtc);
        evidence.Configuration = new ExecutionSamplingConfigurationEvidence(
            WorkerCount,
            PathCount,
            "runtime Sample Profiler default",
            30,
            60,
            120,
            60,
            BenchmarkRandomSeed);
    }

    private static double CalculateTargetThroughputDropPercent(
        IReadOnlyCollection<ExecutionSamplingThroughputMeasurement> measurements)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        var baselineMedian = Median(measurements
            .Where(static measurement => measurement.Mode == "unattached")
            .Select(GetOperationsPerSecond)
            .ToArray());
        var attachedMedian = Median(measurements
            .Where(static measurement => measurement.Mode == "attached")
            .Select(GetOperationsPerSecond)
            .ToArray());
        return ExecutionSamplingThresholds.GetTargetThroughputDropPercent(
            baselineMedian,
            attachedMedian);
    }

    private static double Median(double[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0)
        {
            throw new ArgumentException("At least one throughput value is required.", nameof(values));
        }

        Array.Sort(values);
        var middleIndex = values.Length / 2;
        return values.Length % 2 == 0
            ? values[middleIndex - 1] + ((values[middleIndex] - values[middleIndex - 1]) / 2d)
            : values[middleIndex];
    }

    private static double Percentile95(double[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0)
        {
            throw new ArgumentException("At least one query duration is required.", nameof(values));
        }

        Array.Sort(values);
        var rank = (int)Math.Ceiling(values.Length * 0.95);
        return values[Math.Clamp(rank - 1, 0, values.Length - 1)];
    }

    private static bool HasMonotonicLeak(long[] values)
    {
        if (values.Length < 2 || values[^1] <= values[0])
        {
            return false;
        }

        for (var index = 1; index < values.Length; index++)
        {
            if (values[index] < values[index - 1])
            {
                return false;
            }
        }

        return true;
    }

    private static bool ConcurrentBatchesPassed(
        List<ExecutionSamplingConcurrentQueryBatch> batches) =>
        batches.Count > 0
        && batches.All(static batch =>
            batch.EnteredQueryCount == ConcurrentQueryCount
            && batch.Outcomes.Count(static outcome => outcome.Outcome == "cancelled") == ConcurrentCancellationCount
            && batch.Outcomes.Count(static outcome => outcome.Outcome == "succeeded")
                == ConcurrentQueryCount - ConcurrentCancellationCount
            && batch.Outcomes.All(static outcome =>
                outcome.Outcome == "cancelled"
                || (outcome.Outcome == "succeeded"
                    && outcome.ReceivedSampleCount > 0
                    && outcome.LostEventCount == 0
                    && outcome.CountsConsistent == true)));

    private static void AddThreshold(
        ExecutionSamplingRunEvidence evidence,
        string name,
        bool passed,
        string comparison,
        double actual,
        double limit,
        string unit) =>
        evidence.Thresholds.Add(name, new ExecutionSamplingThresholdDecision(passed, comparison, actual, limit, unit));

    private static async Task<ExecutionSamplingEnvironmentEvidence> CaptureEnvironmentAsync(
        CancellationToken cancellationToken)
    {
        var repositoryRoot = FindRepositoryRoot();
        var gitHead = await RunProcessForOutputAsync(
            "git", ["rev-parse", "HEAD"], repositoryRoot, cancellationToken);
        var gitStatus = await RunProcessForOutputAsync(
            "git", ["status", "--porcelain"], repositoryRoot, cancellationToken);
        var sdkVersion = await RunProcessForOutputAsync(
            "dotnet", ["--version"], repositoryRoot, cancellationToken);
        var processorName = Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
            "ProcessorNameString",
            "unknown")?.ToString() ?? "unknown";
        return new ExecutionSamplingEnvironmentEvidence(
            gitHead.Trim(),
            !string.IsNullOrWhiteSpace(gitStatus),
            sdkVersion.Trim(),
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            processorName.Trim(),
            Environment.ProcessorCount);
    }

    private static async Task<string> RunProcessForOutputAsync(
        string fileName,
        IReadOnlyCollection<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {fileName}.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}: {error}");
        }

        return output;
    }

    private static string FindRepositoryRoot()
    {
        var candidate = new DirectoryInfo(AppContext.BaseDirectory);
        while (candidate is not null)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "DotnetAnalysis.sln")))
            {
                return candidate.FullName;
            }

            candidate = candidate.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the DotnetAnalysis repository root.");
    }

    private static void EnsureNet10RuntimeIsInstalled()
    {
        if (!IntegrationTestHost.GetInstalledRuntimeMajorVersions().Contains(10))
        {
            Assert.Inconclusive("The explicit execution sampling gates require the .NET 10 runtime.");
        }
    }

    private static void RequireExplicitGate(string variableName, string description)
    {
        if (!ExecutionSamplingGate.IsEnabled(Environment.GetEnvironmentVariable(variableName)))
        {
            Assert.Inconclusive($"Set {variableName}=true to run the explicit {description} gate.");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task DeleteDirectoryWithRetriesAsync(string directory)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (
                attempt < 4 && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }
        }
    }

    private enum ExecutionSamplingAttributionMode
    {
        Unattached,
        ExecutionOnly,
        FullAttach
    }

    private sealed record ExecutionSamplingAttributionEvidence(
        IReadOnlyList<ExecutionSamplingAttributionMeasurement> Measurements,
        ExecutionSamplingAttributionSummary Summary);

    private sealed record ExecutionSamplingProviderOnlyAttributionEvidence(
        string ConsumerScope,
        IReadOnlyList<ExecutionSamplingProviderEvidence> Providers,
        bool RequestRundown,
        int CircularBufferMegabytes,
        IReadOnlyList<ExecutionSamplingAttributionMeasurement> Measurements,
        IReadOnlyList<double> PairedThroughputDropPercents,
        double MedianThroughputDropPercent);

    private sealed record ExecutionSamplingProviderEvidence(
        string Name,
        EventLevel EventLevel,
        long Keywords);

    private sealed record ExecutionSamplingAttributionSummary(
        IReadOnlyList<double> ExecutionOnlyPairedThroughputDropPercents,
        IReadOnlyList<double> FullAttachPairedThroughputDropPercents,
        double ExecutionOnlyMedianThroughputDropPercent,
        double FullAttachMedianThroughputDropPercent,
        double ExecutionOnlyMedianDiagnosticAverageCoreUsage,
        double FullAttachMedianDiagnosticAverageCoreUsage,
        double CachedFullRangeQueryP95Milliseconds,
        long CachedFullRangeQueryMaximumAllocatedBytes,
        long MaximumObservedStorageBytes,
        long MaximumProjectedSixtyMinuteStorageBytes);

    private sealed record ExecutionSamplingAttributionQueryMeasurement(
        long SampleCount,
        IReadOnlyList<double> ElapsedMilliseconds,
        IReadOnlyList<long> AllocatedBytes,
        double P95Milliseconds,
        long MaximumAllocatedBytes);

    private sealed record ExecutionSamplingAttributionMeasurement(
        string Mode,
        int Round,
        int OrderPosition,
        string OrderTemperature,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        double DurationSeconds,
        long CompletedOperations,
        double TargetOperationsPerSecond,
        double DiagnosticAverageCoreUsage,
        long DiagnosticAllocatedBytes,
        long StorageBytes,
        long SuccessfulSampleCount,
        long LostEventCount,
        double CaptureDurationSeconds,
        long CachedFullRangeSampleCount,
        IReadOnlyList<double> CachedFullRangeQueryMilliseconds,
        IReadOnlyList<long> CachedFullRangeQueryAllocatedBytes,
        double? CachedFullRangeQueryP95Milliseconds,
        long? CachedFullRangeQueryMaximumAllocatedBytes);

    private sealed class PreparedExecutionWorkloadTarget : IAsyncDisposable
    {
        private const string HostProject = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <PlatformTarget>x64</PlatformTarget>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """;

        private const string HostProgram = """
            using System.Globalization;
            using System.Reflection;

            if (args.Length != 1)
            {
                throw new ArgumentException("A workload assembly path is required.");
            }

            var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            var workloadType = assembly.GetType("ExecutionSamplingWorkload", throwOnError: true)!;
            var start = workloadType.GetMethod("Start", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException(workloadType.FullName, "Start");
            var stop = workloadType.GetMethod("StopAsync", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMethodException(workloadType.FullName, "StopAsync");
            var completed = workloadType.GetField(
                "s_publishedCompletedOperations",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingFieldException(workloadType.FullName, "s_publishedCompletedOperations");
            var workload = start.Invoke(null, null)
                ?? throw new InvalidOperationException("The execution workload did not start.");

            try
            {
                Console.WriteLine("READY");
                string? command;
                while ((command = Console.ReadLine()) is not null)
                {
                    if (string.Equals(command, "EXIT", StringComparison.Ordinal))
                    {
                        break;
                    }

                    var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && string.Equals(parts[0], "COUNT", StringComparison.Ordinal))
                    {
                        Thread.MemoryBarrier();
                        var value = (long)(completed.GetValue(null)
                            ?? throw new InvalidOperationException("The published completion count is unavailable."));
                        Console.WriteLine(string.Create(
                            CultureInfo.InvariantCulture,
                            $"COUNT {parts[1]} {value}"));
                    }
                    else
                    {
                        Console.WriteLine("ERROR unsupported command");
                    }
                }
            }
            finally
            {
                await (Task)(stop.Invoke(workload, null)
                    ?? throw new InvalidOperationException("The execution workload did not stop."));
            }
            """;

        private readonly string _temporaryDirectory;

        private PreparedExecutionWorkloadTarget(
            string temporaryDirectory,
            string executablePath,
            string workloadAssemblyPath)
        {
            _temporaryDirectory = temporaryDirectory;
            ExecutablePath = executablePath;
            WorkloadAssemblyPath = workloadAssemblyPath;
        }

        public string ExecutablePath { get; }

        public string WorkloadAssemblyPath { get; }

        public static async Task<PreparedExecutionWorkloadTarget> CreateAsync(CancellationToken cancellationToken)
        {
            var temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "DotnetAnalysis.Diagnostics.IntegrationTests",
                "ExecutionPerformanceHost",
                Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temporaryDirectory);
                var projectPath = Path.Combine(temporaryDirectory, "ExecutionSamplingCounterHost.csproj");
                var programPath = Path.Combine(temporaryDirectory, "Program.cs");
                await File.WriteAllTextAsync(projectPath, HostProject, cancellationToken);
                await File.WriteAllTextAsync(programPath, HostProgram, cancellationToken);
                await RunProcessForOutputAsync(
                    "dotnet",
                    ["build", projectPath, "--configuration", "Release", "--disable-build-servers", "--nologo", "--verbosity", "quiet"],
                    temporaryDirectory,
                    cancellationToken);
                var executablePath = Path.Combine(
                    temporaryDirectory,
                    "bin",
                    "Release",
                    TargetFramework,
                    "ExecutionSamplingCounterHost.exe");
                if (!File.Exists(executablePath))
                {
                    throw new FileNotFoundException(
                        "The execution sampling counter host was not produced.",
                        executablePath);
                }

                var targetExecutable = IntegrationTestHost.ResolveTargetExecutablePath(TargetFramework);
                var workloadAssemblyPath = Path.ChangeExtension(targetExecutable, ".dll");
                if (!File.Exists(workloadAssemblyPath))
                {
                    throw new FileNotFoundException(
                        "The controlled execution workload assembly was not built.",
                        workloadAssemblyPath);
                }

                return new PreparedExecutionWorkloadTarget(
                    temporaryDirectory,
                    executablePath,
                    workloadAssemblyPath);
            }
            catch
            {
                await DeleteDirectoryWithRetriesAsync(temporaryDirectory);
                throw;
            }
        }

        public async Task<ControlledExecutionWorkloadTarget> StartAsync(CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo(ExecutablePath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(WorkloadAssemblyPath);
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the controlled execution workload target.");
            try
            {
                var errorOutput = process.StandardError.ReadToEndAsync(cancellationToken);
                var ready = await process.StandardOutput
                    .ReadLineAsync(cancellationToken)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                if (!string.Equals(ready, "READY", StringComparison.Ordinal))
                {
                    TryKill(process);
                    var error = await errorOutput;
                    throw new InvalidOperationException(
                        $"The controlled execution workload target did not become ready: {ready}; {error}");
                }

                return new ControlledExecutionWorkloadTarget(
                    process,
                    errorOutput,
                    ExecutablePath,
                    WorkloadAssemblyPath);
            }
            catch
            {
                TryKill(process);
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                process.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync() =>
            await DeleteDirectoryWithRetriesAsync(_temporaryDirectory);
    }

    private sealed class ControlledExecutionWorkloadTarget : IAsyncDisposable
    {
        private readonly SemaphoreSlim _commands = new(1, 1);
        private readonly Task<string> _errorOutput;
        private readonly Process _process;
        private int _nextCommand;
        private int _disposed;

        public ControlledExecutionWorkloadTarget(
            Process process,
            Task<string> errorOutput,
            string executablePath,
            string workloadAssemblyPath)
        {
            _process = process;
            _errorOutput = errorOutput;
            ExecutablePath = executablePath;
            WorkloadAssemblyPath = workloadAssemblyPath;
            ProcessId = process.Id;
            StartTimeUtc = new DateTimeOffset(process.StartTime.ToUniversalTime());
        }

        public int ProcessId { get; }

        public DateTimeOffset StartTimeUtc { get; }

        public string ExecutablePath { get; }

        public string WorkloadAssemblyPath { get; }

        public void Terminate() => TryKill(_process);

        public async Task<long> ReadCompletedOperationsAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _commands.WaitAsync(cancellationToken);
            try
            {
                var command = Interlocked.Increment(ref _nextCommand);
                await _process.StandardInput.WriteLineAsync(
                    string.Create(CultureInfo.InvariantCulture, $"COUNT {command}"));
                await _process.StandardInput.FlushAsync(cancellationToken);
                var response = await _process.StandardOutput
                    .ReadLineAsync(cancellationToken)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                var parts = response?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts is null
                    || parts.Length != 3
                    || !string.Equals(parts[0], "COUNT", StringComparison.Ordinal)
                    || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var responseCommand)
                    || responseCommand != command
                    || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var completed))
                {
                    throw new InvalidDataException($"Invalid workload completion response: {response}");
                }

                return completed;
            }
            finally
            {
                _commands.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _commands.WaitAsync(CancellationToken.None);
            try
            {
                if (!_process.HasExited)
                {
                    await _process.StandardInput.WriteLineAsync("EXIT");
                    await _process.StandardInput.FlushAsync(CancellationToken.None);
                    try
                    {
                        await _process.WaitForExitAsync(CancellationToken.None)
                            .WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
                    }
                    catch (TimeoutException)
                    {
                        TryKill(_process);
                        await _process.WaitForExitAsync(CancellationToken.None)
                            .WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                    }
                }

                var error = await _errorOutput;
                if (_process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"The controlled execution workload target exited with code {_process.ExitCode}: {error}");
                }
            }
            finally
            {
                _process.Dispose();
                _commands.Release();
                _commands.Dispose();
            }
        }
    }

    private sealed class AttachedExecutionFixture : IAsyncDisposable
    {
        private readonly string _executionRoot;
        private readonly EventPipeExecutionSampler _executionSampler;
        private readonly string _snapshotRoot;
        private bool _ended;

        private AttachedExecutionFixture(
            ControlledExecutionWorkloadTarget target,
            IProcessDiagnosticsSession session,
            ExecutionCaptureStore executionStore,
            EventPipeExecutionSampler executionSampler,
            SnapshotStorageLayout snapshotLayout,
            ExecutionQueryEntryGate concurrentQueryEntryGate,
            string executionRoot,
            string snapshotRoot,
            string sessionDirectory,
            DateTimeOffset attachedAtUtc)
        {
            Target = target;
            Session = session;
            ExecutionStore = executionStore;
            _executionSampler = executionSampler;
            SnapshotLayout = snapshotLayout;
            ConcurrentQueryEntryGate = concurrentQueryEntryGate;
            _executionRoot = executionRoot;
            _snapshotRoot = snapshotRoot;
            SessionDirectory = sessionDirectory;
            AttachedAtUtc = attachedAtUtc;
        }

        public ControlledExecutionWorkloadTarget Target { get; }

        public IProcessDiagnosticsSession Session { get; }

        public ExecutionCaptureStore ExecutionStore { get; }

        public SnapshotStorageLayout SnapshotLayout { get; }

        public ExecutionQueryEntryGate ConcurrentQueryEntryGate { get; }

        public EventPipeExecutionSampler ExecutionSampler => _executionSampler;

        public string SessionDirectory { get; }

        public DateTimeOffset AttachedAtUtc { get; }

        public long FinalSuccessfulSampleCount => _ended
            ? _executionSampler.SuccessfulSampleCount
            : throw new InvalidOperationException("Final sampler state is available only after EndAsync completes.");

        public long FinalLostEventCount => _ended
            ? _executionSampler.LostEventCount
            : throw new InvalidOperationException("Final sampler state is available only after EndAsync completes.");

        public static async Task<AttachedExecutionFixture> StartAsync(
            PreparedExecutionWorkloadTarget preparedTarget,
            CancellationToken cancellationToken)
        {
            var target = await preparedTarget.StartAsync(cancellationToken);
            var root = Path.Combine(
                Path.GetTempPath(),
                "DotnetAnalysis.Diagnostics.IntegrationTests",
                "ExecutionPerformance",
                Guid.NewGuid().ToString("N"));
            var executionRoot = Path.Combine(root, "execution");
            var snapshotRoot = Path.Combine(root, "snapshots");
            IProcessDiagnosticsSession? session = null;
            ExecutionSamplingSession? executionSamplingSession = null;
            ExecutionCaptureStore? executionStore = null;
            EventPipeExecutionSampler? executionSampler = null;
            ExecutionCaptureStorageLayout? executionLayout = null;
            var concurrentQueryEntryGate = new ExecutionQueryEntryGate();
            try
            {
                var snapshotLayout = new SnapshotStorageLayout(snapshotRoot);
                var eventBus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
                var diagnostics = new WindowsProcessDiagnostics(
                    eventBus,
                    TimeProvider.System,
                    () =>
                    {
                        executionLayout = new ExecutionCaptureStorageLayout(executionRoot);
                        executionStore = new ExecutionCaptureStore(
                            executionLayout,
                            beforeStackFramesReadAsync: concurrentQueryEntryGate.WaitIfArmedAsync);
                        executionSampler = new EventPipeExecutionSampler(executionStore);
                        executionSamplingSession = new ExecutionSamplingSession(
                            executionStore,
                            executionSampler,
                            new ExecutionSymbolResolver());
                        return executionSamplingSession;
                    },
                    snapshotLayout: snapshotLayout);
                var targetProcess = (await diagnostics.GetProcessesAsync(cancellationToken))
                    .Single(process => process.ProcessId == target.ProcessId);
                session = await diagnostics.AttachAsync(targetProcess, cancellationToken)
                    .WaitAsync(s_cleanupStepTimeout, cancellationToken);
                if (session is not ProcessDiagnosticsSession)
                {
                    throw new InvalidOperationException(
                        "The performance gate must use the production process diagnostics session.");
                }

                var attachedAtUtc = DateTimeOffset.UtcNow;
                if (executionStore is null || executionSampler is null || executionLayout is null)
                {
                    throw new InvalidOperationException(
                        "The production execution sampling factory did not create its sampler and storage layout.");
                }

                return new AttachedExecutionFixture(
                    target,
                    session,
                    executionStore,
                    executionSampler,
                    snapshotLayout,
                    concurrentQueryEntryGate,
                    executionRoot,
                    snapshotRoot,
                    executionLayout.SessionDirectory,
                    attachedAtUtc);
            }
            catch (Exception exception)
            {
                var failures = new List<Exception> { exception };
                Task? sessionCleanup = null;
                var sessionResource = (IAsyncDisposable?)session ?? executionSamplingSession;
                if (sessionResource is not null)
                {
                    try
                    {
                        sessionCleanup = sessionResource.DisposeAsync().AsTask();
                        await sessionCleanup.WaitAsync(s_cleanupStepTimeout, CancellationToken.None);
                    }
                    catch (Exception cleanupException)
                    {
                        failures.Add(cleanupException);
                    }
                }

                try
                {
                    await target.DisposeAsync().AsTask()
                        .WaitAsync(s_cleanupStepTimeout, CancellationToken.None);
                }
                catch (Exception cleanupException)
                {
                    target.Terminate();
                    failures.Add(cleanupException);
                }

                if (sessionCleanup is { IsCompleted: false })
                {
                    try
                    {
                        await sessionCleanup.WaitAsync(s_cleanupStepTimeout, CancellationToken.None);
                    }
                    catch (Exception cleanupException)
                    {
                        failures.Add(cleanupException);
                    }
                }

                try
                {
                    await DeleteDirectoryWithRetriesAsync(root);
                }
                catch (Exception cleanupException)
                {
                    failures.Add(cleanupException);
                }

                if (failures.Count == 1)
                {
                    ExceptionDispatchInfo.Capture(exception).Throw();
                }

                throw new AggregateException("Execution performance fixture setup and cleanup failed.", failures);
            }
        }

        public ExecutionSamplingTargetEvidence CreateTargetEvidence() =>
            new(
                Target.ExecutablePath,
                Target.WorkloadAssemblyPath,
                TargetFramework,
                Target.ProcessId,
                Target.StartTimeUtc);

        public async Task EndAsync(CancellationToken cancellationToken)
        {
            if (_ended)
            {
                return;
            }

            await Session.EndAsync(cancellationToken)
                .WaitAsync(s_cleanupStepTimeout, cancellationToken);
            _ended = true;
        }

        public async ValueTask DisposeAsync()
        {
            var failures = new List<Exception>();
            Task? endTask = null;
            if (!_ended)
            {
                try
                {
                    endTask = Session.EndAsync(CancellationToken.None);
                    await endTask.WaitAsync(s_cleanupStepTimeout, CancellationToken.None);
                    _ended = true;
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            try
            {
                await Target.DisposeAsync().AsTask()
                    .WaitAsync(s_cleanupStepTimeout, CancellationToken.None);
            }
            catch (Exception exception)
            {
                Target.Terminate();
                failures.Add(exception);
            }

            if (endTask is { IsCompleted: false })
            {
                try
                {
                    await endTask.WaitAsync(s_cleanupStepTimeout, CancellationToken.None);
                    _ended = true;
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            if (endTask is not { IsCompleted: false })
            {
                try
                {
                    await Session.DisposeAsync().AsTask()
                        .WaitAsync(s_cleanupStepTimeout, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            try
            {
                await DeleteDirectoryWithRetriesAsync(Path.GetDirectoryName(_executionRoot)!);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            if (Directory.Exists(_executionRoot) || Directory.Exists(_snapshotRoot))
            {
                failures.Add(new IOException(
                    "The execution performance fixture did not remove its temporary roots."));
            }

            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }

            if (failures.Count > 1)
            {
                throw new AggregateException("Execution performance fixture cleanup failed.", failures);
            }
        }
    }
}

internal sealed class ExecutionQueryEntryGate
{
    private readonly object _syncRoot = new();
    private TaskCompletionSource? _allQueriesEntered;
    private TaskCompletionSource? _releaseQueries;
    private int _expectedQueryCount;
    private int _enteredQueryCount;
    private bool _isArmed;

    public int EnteredQueryCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _enteredQueryCount;
            }
        }
    }

    public void Arm(int expectedQueryCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedQueryCount);
        lock (_syncRoot)
        {
            if (_isArmed)
            {
                throw new InvalidOperationException("The execution query entry gate is already armed.");
            }

            _expectedQueryCount = expectedQueryCount;
            _enteredQueryCount = 0;
            _allQueriesEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _releaseQueries = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _isArmed = true;
        }
    }

    public Task WaitForAllQueriesEnteredAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            if (!_isArmed)
            {
                throw new InvalidOperationException("The execution query entry gate is not armed.");
            }

            return _allQueriesEntered!.Task.WaitAsync(timeout, cancellationToken);
        }
    }

    public Task WaitIfArmedAsync()
    {
        lock (_syncRoot)
        {
            if (!_isArmed)
            {
                return Task.CompletedTask;
            }

            if (_enteredQueryCount < _expectedQueryCount)
            {
                _enteredQueryCount++;
                if (_enteredQueryCount == _expectedQueryCount)
                {
                    _allQueriesEntered!.TrySetResult();
                }
            }

            return _releaseQueries!.Task;
        }
    }

    public void Release()
    {
        lock (_syncRoot)
        {
            _releaseQueries?.TrySetResult();
        }
    }

    public void Disarm()
    {
        lock (_syncRoot)
        {
            _releaseQueries?.TrySetResult();
            _isArmed = false;
            _allQueriesEntered = null;
            _releaseQueries = null;
            _expectedQueryCount = 0;
        }
    }
}
