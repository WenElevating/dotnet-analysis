using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.DependencyInjection;
using DotnetAnalysis.Diagnostics.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using System.Diagnostics.Tracing;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class WindowsDiagnosticsIntegrationTests
{
    private static readonly JsonSerializerOptions s_indentedJson = new() { WriteIndented = true };

    [TestMethod]
    [TestCategory("MemorySnapshotPerformance")]
    [DoNotParallelize]
    public async Task LargeSnapshotTransportProbe_SeparatesEventPipeTransportFromLiveGraphConstruction()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("DOTNET_ANALYSIS_RUN_LARGE_SNAPSHOT_TRANSPORT_PROBE"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive("Set DOTNET_ANALYSIS_RUN_LARGE_SNAPSHOT_TRANSPORT_PROBE=true to run the large snapshot transport probe.");
        }

        const int objectCount = 1_000_000;
        const int measurementRuns = 5;
        var rawCaptures = new List<HeapSnapshotTransportResult>();
        var liveGraphCaptures = new List<HeapSnapshotTransportResult>();
        var edgeCompleteCaptures = new List<HeapSnapshotTransportResult>();

        await using (var target = await IntegrationTestHost.StartTargetAsync("net10.0", objectCount))
        {
            for (var run = 0; run < measurementRuns; run++)
            {
                rawCaptures.Add(await CaptureHeapSnapshotTransportAsync(target.ProcessId, buildLiveGraph: false));
            }
        }

        await using (var target = await IntegrationTestHost.StartTargetAsync("net10.0", objectCount))
        {
            for (var run = 0; run < measurementRuns; run++)
            {
                liveGraphCaptures.Add(await CaptureHeapSnapshotTransportAsync(target.ProcessId, buildLiveGraph: true));
            }
        }

        await using (var target = await IntegrationTestHost.StartTargetAsync("net10.0", objectCount))
        {
            for (var run = 0; run < measurementRuns; run++)
            {
                edgeCompleteCaptures.Add(await CaptureHeapSnapshotTransportAsync(
                    target.ProcessId,
                    buildLiveGraph: true,
                    stopWhenAllDeclaredEdgesArrive: true));
            }
        }

        await WriteHeapSnapshotTransportProbeArtifactAsync(rawCaptures, liveGraphCaptures, edgeCompleteCaptures);
        Assert.IsTrue(edgeCompleteCaptures.All(result => result.Completed), FormatTransportProbeFailure("edge-complete stop", edgeCompleteCaptures));
        Assert.IsTrue(edgeCompleteCaptures.All(result => result.StoppedWhenAllDeclaredEdgesArrived), FormatTransportProbeFailure("edge-complete stop trigger", edgeCompleteCaptures));
        Assert.IsTrue(edgeCompleteCaptures.All(result => result.EventsLost == 0), FormatTransportProbeFailure("edge-complete event loss", edgeCompleteCaptures));
        Assert.IsTrue(edgeCompleteCaptures.All(result => result.RootCount > 0), FormatTransportProbeFailure("edge-complete root preservation", edgeCompleteCaptures));
    }

    [TestMethod]
    [TestCategory("MemorySnapshotPerformance")]
    public async Task LargeSnapshotBenchmark_RecordsCaptureAndCachedQueryMeasurements()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("DOTNET_ANALYSIS_RUN_LARGE_SNAPSHOT_BENCHMARK"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive("Set DOTNET_ANALYSIS_RUN_LARGE_SNAPSHOT_BENCHMARK=true to run the large snapshot benchmark.");
        }

        const int objectCount = 1_000_000;
        const int measurementRuns = 9;
        const double captureBaselineP50Milliseconds = 1600.4381;
        var enforceCaptureBaseline = string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_ANALYSIS_ENFORCE_CAPTURE_BASELINE"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        await using var target = await IntegrationTestHost.StartTargetAsync("net10.0", objectCount);
        var snapshotRoot = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.LargeSnapshotBenchmark", Guid.NewGuid().ToString("N"));
        var layout = new DotnetAnalysis.Diagnostics.Windows.SnapshotStorageLayout(snapshotRoot);
        var services = new ServiceCollection();
        services.AddSingleton<IEventBus>(_ => new InProcessEventBus(NullLogger<InProcessEventBus>.Instance));
        services.AddSingleton(layout);
        services.AddWindowsProcessDiagnostics();
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var diagnostics = provider.GetRequiredService<IProcessDiagnostics>();
        var process = (await diagnostics.GetProcessesAsync(CancellationToken.None))
            .Single(candidate => candidate.ProcessId == target.ProcessId);
        await using var session = await diagnostics.AttachAsync(process, CancellationToken.None);
        var analysisService = provider.GetRequiredService<IMemorySnapshotAnalysisService>();
        var captures = new List<double>();
        var analyses = new List<double>();
        var runnerPrivateMemoryBytes = new List<long>();
        long pageAllocatedBytes = 0;
        long referencePathAllocatedBytes = 0;

        try
        {
            var warmupSnapshot = await session.CaptureSnapshotAsync(CancellationToken.None);
            _ = await analysisService.AnalyzeAsync(warmupSnapshot, CancellationToken.None);

            for (var run = 0; run < measurementRuns; run++)
            {
                MemorySnapshot snapshot;
                try
                {
                    var captureStopwatch = Stopwatch.StartNew();
                    snapshot = await session.CaptureSnapshotAsync(CancellationToken.None);
                    captureStopwatch.Stop();
                    captures.Add(captureStopwatch.Elapsed.TotalMilliseconds);
                }
                catch (DiagnosticsException exception)
                {
                    throw new AssertFailedException(
                        $"Large snapshot capture {run + 1} of {measurementRuns} failed. Completed capture times: {string.Join(", ", captures)}. Runner private memory: {string.Join(", ", runnerPrivateMemoryBytes)}. Capture diagnostics: {exception}",
                        exception);
                }

                var analysisStopwatch = Stopwatch.StartNew();
                var analysis = await analysisService.AnalyzeAsync(snapshot, CancellationToken.None);
                analysisStopwatch.Stop();
                analyses.Add(analysisStopwatch.Elapsed.TotalMilliseconds);
                Assert.AreEqual(MemorySnapshotObjectAccessMode.Paged, analysis.ObjectAccessMode);

                var largestType = analysis.Types.OrderByDescending(summary => summary.ObjectCount).First().Type;
                var fullEnumerationException = await Assert.ThrowsAsync<DiagnosticsException>(
                    async () => await analysisService.GetObjectsAsync(snapshot, largestType, CancellationToken.None));
                Assert.AreEqual(DiagnosticsErrorCode.SnapshotTooLargeForFullEnumeration, fullEnumerationException.ErrorCode);

                _ = await analysisService.GetObjectsPageAsync(snapshot, largestType, 0, 1, CancellationToken.None);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var beforePage = GC.GetTotalAllocatedBytes(precise: true);
                var page = await analysisService.GetObjectsPageAsync(snapshot, largestType, 0, 1000, CancellationToken.None);
                pageAllocatedBytes = Math.Max(
                    pageAllocatedBytes,
                    GC.GetTotalAllocatedBytes(precise: true) - beforePage);
                Assert.HasCount(1000, page.Objects);

                _ = await analysisService.GetReferencePathAsync(snapshot, page.Objects[0].Address, CancellationToken.None);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var beforeReferencePath = GC.GetTotalAllocatedBytes(precise: true);
                _ = await analysisService.GetReferencePathAsync(snapshot, page.Objects[0].Address, CancellationToken.None);
                referencePathAllocatedBytes = Math.Max(
                    referencePathAllocatedBytes,
                    GC.GetTotalAllocatedBytes(precise: true) - beforeReferencePath);
                runnerPrivateMemoryBytes.Add(Process.GetCurrentProcess().PrivateMemorySize64);
            }

            Assert.IsLessThan(1024L * 1024, pageAllocatedBytes);
            Assert.IsLessThan(16L * 1024 * 1024, referencePathAllocatedBytes);
            await WriteLargeSnapshotBenchmarkArtifactAsync(
                objectCount,
                captures,
                analyses,
                runnerPrivateMemoryBytes,
                pageAllocatedBytes,
                referencePathAllocatedBytes);
            if (enforceCaptureBaseline)
            {
                Assert.IsLessThan(
                    captureBaselineP50Milliseconds,
                    GetP50(captures),
                    "The direct EventPipe heap capture P50 must improve on the pre-change 1600.4381 ms baseline.");
            }
        }
        finally
        {
            await session.EndAsync(CancellationToken.None);
            if (Directory.Exists(snapshotRoot))
            {
                Directory.Delete(snapshotRoot, recursive: true);
            }
        }
    }
    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    [DataRow("net8.0")]
    [DataRow("net10.0")]
    public async Task AttachedTarget_StartsAndCleansUp(string targetFramework)
    {
        await using var target = await IntegrationTestHost.StartTargetAsync(targetFramework);
        Assert.IsGreaterThan(0, target.ProcessId);
    }

    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    [DataRow("net8.0")]
    [DataRow("net10.0")]
    public async Task AttachedTarget_ReportsMeasuredEventPipeAndPrivateWorkingSetSample(string targetFramework)
    {
        await using var target = await IntegrationTestHost.StartTargetAsync(targetFramework);
        var services = new ServiceCollection();
        services.AddSingleton<IEventBus>(_ =>
            new InProcessEventBus(NullLogger<InProcessEventBus>.Instance));
        services.AddWindowsProcessDiagnostics();
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var diagnostics = provider.GetRequiredService<IProcessDiagnostics>();

        var process = (await diagnostics.GetProcessesAsync(CancellationToken.None))
            .Single(candidate => candidate.ProcessId == target.ProcessId);
        await using var session = await diagnostics.AttachAsync(process, CancellationToken.None);
        await using var samples = session.GetMemoryUsageAsync(CancellationToken.None).GetAsyncEnumerator();

        MemoryUsageSample? measured = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.IsTrue(await samples.MoveNextAsync());
            if (samples.Current.State == MemoryUsageSampleState.Measured)
            {
                measured = samples.Current;
                break;
            }
        }

        Assert.IsNotNull(measured);
        Assert.IsGreaterThan(0L, measured.ProcessMemoryBytes!.Value);
        Assert.IsGreaterThanOrEqualTo(0L, measured.ManagedHeapBytes!.Value);
    }

    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    public async Task Net9_Target_StartsWhenRuntimeIsAvailable()
    {
        if (!IntegrationTestHost.GetInstalledRuntimeMajorVersions().Contains(9))
        {
            Assert.Inconclusive("net9 runtime is reserved and optional on this machine.");
        }

        await using var target = await IntegrationTestHost.StartTargetAsync("net9.0");
        Assert.IsGreaterThan(0, target.ProcessId);
    }

    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    public void Net10_TargetExecutablePath_IsResolvable()
    {
        var executablePath = IntegrationTestHost.ResolveTargetExecutablePath("net10.0");

        Assert.IsTrue(File.Exists(executablePath));
        Assert.IsTrue(executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    [DataRow("net8.0")]
    [DataRow("net9.0")]
    [DataRow("net10.0")]
    public async Task Target_LiveCaptureReopensAndAnalyzesEventPipeHeap(string targetFramework)
    {
        if (!IntegrationTestHost.GetInstalledRuntimeMajorVersions().Contains(10))
        {
            Assert.Inconclusive("net10 runtime is required for this integration test.");
        }

        await using var target = await IntegrationTestHost.StartTargetAsync(targetFramework);
        var snapshotRoot = Path.Combine(
            Path.GetTempPath(),
            "DotnetAnalysis.Diagnostics.IntegrationTests",
            Guid.NewGuid().ToString("N"));
        var layout = new DotnetAnalysis.Diagnostics.Windows.SnapshotStorageLayout(snapshotRoot);
        var services = new ServiceCollection();
        services.AddSingleton<IEventBus>(_ =>
            new InProcessEventBus(NullLogger<InProcessEventBus>.Instance));
        services.AddSingleton(layout);
        services.AddWindowsProcessDiagnostics();
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var diagnostics = provider.GetRequiredService<IProcessDiagnostics>();

        var process = (await diagnostics.GetProcessesAsync(CancellationToken.None))
            .Single(candidate => candidate.ProcessId == target.ProcessId);

        await using var session = await diagnostics.AttachAsync(process, CancellationToken.None);
        await using var sampleEnumerator = session.GetMemoryUsageAsync(CancellationToken.None).GetAsyncEnumerator();
        MemoryUsageSample? sample = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.IsTrue(await sampleEnumerator.MoveNextAsync());
            sample = sampleEnumerator.Current;
            if (sample.State == MemoryUsageSampleState.Measured)
            {
                break;
            }
        }

        Assert.IsNotNull(sample);
        Assert.AreEqual(MemoryUsageSampleState.Measured, sample.State);
        Assert.IsGreaterThan(0L, sample.ProcessMemoryBytes!.Value);
        Assert.IsGreaterThanOrEqualTo(0L, sample.ManagedHeapBytes!.Value);

        var snapshot = await session.CaptureSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(MemorySnapshotOrigin.Captured, snapshot.Origin);
        Assert.AreEqual(MemorySnapshotState.Analyzing, snapshot.State);

        var capturedBytes = await File.ReadAllBytesAsync(layout.GetFinalDumpPath(snapshot.Id));
        Assert.IsTrue(
            Encoding.UTF8.GetString(capturedBytes).Contains("GCHeapDump", StringComparison.Ordinal),
            "Captured snapshots must use the official FastSerialization GCHeapDump envelope.");

        var analysis = await provider.GetRequiredService<IMemorySnapshotAnalysisService>()
            .AnalyzeAsync(snapshot, CancellationToken.None);
        var typeSummaries = analysis.Types;
        Assert.AreNotEqual(AllocationProfileDataQuality.NotAvailable, analysis.AllocationProfile.DataQuality);
        Assert.AreEqual(MemorySnapshotState.Ready, analysis.Snapshot.State);
        Assert.IsGreaterThan(0, typeSummaries.Count);
        var topType = typeSummaries[0].Type;
        var objects = await provider.GetRequiredService<IMemorySnapshotAnalysisService>()
            .GetObjectsAsync(snapshot, topType, CancellationToken.None);
        Assert.IsGreaterThan(0, objects.Count);

        var analysisService = provider.GetRequiredService<IMemorySnapshotAnalysisService>();
        MemoryReferencePath? referencePath = null;
        foreach (var summary in typeSummaries.Take(32))
        {
            var candidates = await analysisService.GetObjectsAsync(snapshot, summary.Type, CancellationToken.None);
            foreach (var candidate in candidates.Take(8))
            {
                referencePath = await analysisService.GetReferencePathAsync(
                    snapshot,
                    candidate.Address,
                    CancellationToken.None);
                if (referencePath is { Objects.Count: > 1 })
                {
                    break;
                }
            }

            if (referencePath is { Objects.Count: > 1 })
            {
                break;
            }
        }

        if (referencePath is not null)
        {
            Assert.AreEqual(referencePath.TargetObjectAddress, referencePath.Objects[^1].Address);
        }

        var reopened = await diagnostics.OpenSnapshotAsync(
            layout.GetFinalDumpPath(snapshot.Id),
            CancellationToken.None);
        var reopenedAnalysis = await provider.GetRequiredService<IMemorySnapshotAnalysisService>()
            .AnalyzeAsync(reopened, CancellationToken.None);
        Assert.AreEqual(MemorySnapshotOrigin.Captured, reopened.Origin);
        Assert.AreEqual(snapshot.Id, reopened.Id);
        Assert.AreEqual(snapshot.RequestedAtUtc, reopened.RequestedAtUtc);
        Assert.AreEqual(snapshot.CaptureStartedAtUtc, reopened.CaptureStartedAtUtc);
        Assert.AreEqual(snapshot.CapturedAtUtc, reopened.CapturedAtUtc);
        Assert.AreEqual(MemorySnapshotState.Ready, reopenedAnalysis.Snapshot.State);
        Assert.AreNotEqual(0, reopenedAnalysis.Types.Count);
        Assert.AreEqual(
            analysis.AllocationProfile.DataQuality,
            reopenedAnalysis.AllocationProfile.DataQuality);
        Assert.AreEqual(
            analysis.AllocationProfile.CallStackQuality,
            reopenedAnalysis.AllocationProfile.CallStackQuality);

        await session.EndAsync(CancellationToken.None);
        Assert.IsFalse(Directory.EnumerateFiles(snapshotRoot, "*.tmp.*", SearchOption.AllDirectories).Any());
        Assert.IsTrue(File.Exists(layout.GetFinalDumpPath(snapshot.Id)));
    }

    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    public async Task Net10TargetMeasuredSamplesMatchNativePrivateWorkingSet()
    {
        await using var target = await IntegrationTestHost.StartTargetAsync("net10.0");
        var services = new ServiceCollection();
        services.AddSingleton<IEventBus>(_ =>
            new InProcessEventBus(NullLogger<InProcessEventBus>.Instance));
        services.AddWindowsProcessDiagnostics();
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var diagnostics = provider.GetRequiredService<IProcessDiagnostics>();
        var process = (await diagnostics.GetProcessesAsync(CancellationToken.None))
            .Single(candidate => candidate.ProcessId == target.ProcessId);
        await using var session = await diagnostics.AttachAsync(process, CancellationToken.None);
        await using var samples = session.GetMemoryUsageAsync(CancellationToken.None).GetAsyncEnumerator();
        var evidence = new List<PrivateWorkingSetEvidence>();
        var reader = new ProcessMemoryReader();

        for (var index = 0; index < 3; index++)
        {
            _ = await GetNextMeasuredSampleAsync(samples);
            var observedAtUtc = DateTimeOffset.UtcNow;
            var diagnosticsBytes = reader.ReadPrivateWorkingSetBytes(target.ProcessId);
            Assert.IsNotNull(diagnosticsBytes);
            var nativeBytes = ReadNativePrivateWorkingSetBytes(target.ProcessId);
            var diagnosticMegabytes = Math.Round(diagnosticsBytes.Value / 1024d / 1024d);
            var nativeMegabytes = Math.Round(nativeBytes / 1024d / 1024d);
            evidence.Add(new PrivateWorkingSetEvidence(
                observedAtUtc,
                diagnosticsBytes.Value,
                nativeBytes,
                diagnosticMegabytes,
                nativeMegabytes,
                Math.Abs(diagnosticMegabytes - nativeMegabytes)));

            if (index < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }

        var artifactDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "TestResults",
            "DiagnosticsTaskManagerCrossCheck-20260903");
        Directory.CreateDirectory(artifactDirectory);
        var artifact = new
        {
            capturedAtUtc = DateTimeOffset.UtcNow,
            targetFramework = "net10.0",
            pid = target.ProcessId,
            windowsBuild = Environment.OSVersion.Version.Build,
            windowsVersion = Environment.OSVersion.Version.ToString(),
            windowsArchitecture = Environment.Is64BitOperatingSystem ? "x64" : "x86",
            taskManagerUiAutomation = "Taskmgr Details virtualized list is not exposed by UI Automation on this host; native PROCESS_MEMORY_COUNTERS_EX2.PrivateWorkingSetSize is used as the equivalent metric.",
            samples = evidence
        };
        await File.WriteAllTextAsync(
            Path.Combine(artifactDirectory, "task-manager-cross-check.json"),
            JsonSerializer.Serialize(artifact, s_indentedJson));

        foreach (var sample in evidence)
        {
            Assert.IsLessThanOrEqualTo(
                1d,
                sample.DifferenceMegabytes,
                $"{sample.ObservedAtUtc:O}: diagnostics={sample.DiagnosticsMegabytes} MB, native={sample.NativeMegabytes} MB");
        }
    }

    private static async Task<MemoryUsageSample> GetNextMeasuredSampleAsync(
        IAsyncEnumerator<MemoryUsageSample> samples)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.IsTrue(await samples.MoveNextAsync());
            if (samples.Current.State == MemoryUsageSampleState.Measured)
            {
                return samples.Current;
            }
        }

        Assert.Fail("No measured memory sample was observed.");
        return null!;
    }


    private static Task WriteLargeSnapshotBenchmarkArtifactAsync(
        int objectCount,
        List<double> captures,
        List<double> analyses,
        List<long> runnerPrivateMemoryBytes,
        long pageAllocatedBytes,
        long referencePathAllocatedBytes)
    {
        var artifactDirectory = Path.Combine(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
            "TestResults",
            "LargeSnapshotBenchmark-20260904");
        Directory.CreateDirectory(artifactDirectory);
        var artifact = new
        {
            capturedAtUtc = DateTimeOffset.UtcNow,
            objectCount,
            objectPayloadBytes = 256,
            measurementRuns = captures.Count,
            warmupRuns = 1,
            sdkVersion = Environment.Version.ToString(),
            windowsBuild = Environment.OSVersion.Version.Build,
            windowsVersion = RuntimeInformation.OSDescription,
            processorCount = Environment.ProcessorCount,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            captureMilliseconds = captures,
            captureP50Milliseconds = GetP50(captures),
            analysisMilliseconds = analyses,
            analysisP50Milliseconds = GetP50(analyses),
            runnerPrivateMemoryBytes,
            cachedPageAllocatedBytes = pageAllocatedBytes,
            cachedReferencePathAllocatedBytes = referencePathAllocatedBytes
        };
        return File.WriteAllTextAsync(
            Path.Combine(artifactDirectory, "large-snapshot-benchmark.json"),
            JsonSerializer.Serialize(artifact, s_indentedJson));
    }

    private static async Task<HeapSnapshotTransportResult> CaptureHeapSnapshotTransportAsync(
        int processId,
        bool buildLiveGraph,
        bool stopWhenAllDeclaredEdgesArrive = false)
    {
        using var session = new DiagnosticsClient(processId).StartEventPipeSession(
            new EventPipeProvider(
                "Microsoft-Windows-DotNETRuntime",
                EventLevel.Verbose,
                (long)ClrTraceEventParser.Keywords.GCHeapSnapshot),
            requestRundown: true,
            circularBufferMB: 128);
        using var source = new EventPipeEventSource(session.EventStream);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allDeclaredEdgesReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawHeapNode = 0;
        EventPipeHeapBuilder? heapBuilder = null;
        if (buildLiveGraph)
        {
            heapBuilder = new EventPipeHeapBuilder();
            heapBuilder.Attach(source);
        }
        else
        {
            source.Clr.GCBulkNode += data =>
            {
                if (data.Count > 0)
                {
                    Volatile.Write(ref sawHeapNode, 1);
                }
            };
        }

        source.Clr.GCStop += _ =>
        {
            if (buildLiveGraph ? heapBuilder!.HasHeapData : Volatile.Read(ref sawHeapNode) != 0)
            {
                completed.TrySetResult();
            }
        };
        if (stopWhenAllDeclaredEdgesArrive)
        {
            source.Clr.GCBulkEdge += _ =>
            {
                if (heapBuilder!.HasReceivedAllDeclaredEdges)
                {
                    allDeclaredEdgesReceived.TrySetResult();
                }
            };
        }

        var stopwatch = Stopwatch.StartNew();
        var processing = Task.Run(() => ProcessHeapSnapshotTransport(source), CancellationToken.None);
        var winner = await Task.WhenAny(
            completed.Task,
            allDeclaredEdgesReceived.Task,
            processing,
            Task.Delay(TimeSpan.FromSeconds(30)));
        var stoppedWhenAllDeclaredEdgesArrived = winner == allDeclaredEdgesReceived.Task;
        if (stoppedWhenAllDeclaredEdgesArrived)
        {
            StopHeapSnapshotTransportSession(session);
            await processing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        stopwatch.Stop();
        if (completed.Task.IsCompletedSuccessfully)
        {
            StopHeapSnapshotTransportSession(session);

            await processing.WaitAsync(TimeSpan.FromSeconds(5));
            long? rootCount = heapBuilder is null ? null : heapBuilder.Build().Roots.Count;
            return HeapSnapshotTransportResult.Success(
                stopwatch.Elapsed.TotalMilliseconds,
                source.EventsLost,
                rootCount,
                stoppedWhenAllDeclaredEdgesArrived);
        }

        var diagnostics = heapBuilder?.GetDiagnostics();
        return HeapSnapshotTransportResult.Failure(
            stopwatch.Elapsed.TotalMilliseconds,
            source.EventsLost,
            diagnostics?.GcStopCount,
            diagnostics?.NodeCount,
            diagnostics?.EdgeCount,
            diagnostics?.RootEdgeCount,
            diagnostics?.LastEdgeObservedMilliseconds,
            stoppedWhenAllDeclaredEdgesArrived);
    }

    private static void ProcessHeapSnapshotTransport(EventPipeEventSource source)
    {
        try
        {
            source.Process();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or IOException
                or EndOfStreamException)
        {
        }
    }

    private static void StopHeapSnapshotTransportSession(EventPipeSession session)
    {
        try
        {
            session.Stop();
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or DiagnosticsClientException)
        {
        }
    }

    private static Task WriteHeapSnapshotTransportProbeArtifactAsync(
        List<HeapSnapshotTransportResult> rawCaptures,
        List<HeapSnapshotTransportResult> liveGraphCaptures,
        List<HeapSnapshotTransportResult> edgeCompleteCaptures)
    {
        var artifactDirectory = Path.Combine(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
            "TestResults",
            "LargeSnapshotBenchmark-20260904");
        Directory.CreateDirectory(artifactDirectory);
        return File.WriteAllTextAsync(
            Path.Combine(artifactDirectory, "heap-snapshot-transport-probe.json"),
            JsonSerializer.Serialize(new
            {
                capturedAtUtc = DateTimeOffset.UtcNow,
                rawCaptures,
                liveGraphCaptures,
                edgeCompleteCaptures
            }, s_indentedJson));
    }

    private static string FormatTransportProbeFailure(string mode, List<HeapSnapshotTransportResult> results) =>
        $"{mode} transport results: {JsonSerializer.Serialize(results, s_indentedJson)}";

    private static double GetP50(List<double> values) =>
        values.OrderBy(value => value).ElementAt(values.Count / 2);

    private sealed record HeapSnapshotTransportResult(
        bool Completed,
        double ElapsedMilliseconds,
        long EventsLost,
        long? GcStopCount,
        long? NodeCount,
        long? EdgeCount,
        long? RootEdgeCount,
        double? LastEdgeObservedMilliseconds,
        long? RootCount,
        bool StoppedWhenAllDeclaredEdgesArrived)
    {
        public static HeapSnapshotTransportResult Success(
            double elapsedMilliseconds,
            long eventsLost,
            long? rootCount,
            bool stoppedWhenAllDeclaredEdgesArrived) =>
            new(true, elapsedMilliseconds, eventsLost, null, null, null, null, null, rootCount, stoppedWhenAllDeclaredEdgesArrived);

        public static HeapSnapshotTransportResult Failure(
            double elapsedMilliseconds,
            long eventsLost,
            long? gcStopCount,
            long? nodeCount,
            long? edgeCount,
            long? rootEdgeCount,
            double? lastEdgeObservedMilliseconds,
            bool stoppedWhenAllDeclaredEdgesArrived) =>
            new(false, elapsedMilliseconds, eventsLost, gcStopCount, nodeCount, edgeCount, rootEdgeCount, lastEdgeObservedMilliseconds, null, stoppedWhenAllDeclaredEdgesArrived);
    }

    private static long ReadNativePrivateWorkingSetBytes(int processId)
    {
        using var process = Process.GetProcessById(processId);
        var counters = new ProcessMemoryCountersEx2
        {
            StructureLength = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>()
        };
        Assert.IsTrue(GetProcessMemoryInfo(process.Handle, ref counters, counters.StructureLength));
        return checked((long)counters.PrivateWorkingSetSize);
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(
        IntPtr process,
        ref ProcessMemoryCountersEx2 counters,
        uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2
    {
        public uint StructureLength;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    private sealed record PrivateWorkingSetEvidence(
        DateTimeOffset ObservedAtUtc,
        long DiagnosticsBytes,
        long NativeBytes,
        double DiagnosticsMegabytes,
        double NativeMegabytes,
        double DifferenceMegabytes);
}
