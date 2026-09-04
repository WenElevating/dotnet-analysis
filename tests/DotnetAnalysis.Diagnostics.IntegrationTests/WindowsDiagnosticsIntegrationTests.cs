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

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class WindowsDiagnosticsIntegrationTests
{
    private static readonly JsonSerializerOptions s_indentedJson = new() { WriteIndented = true };
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

        Assert.IsNotNull(referencePath);
        Assert.IsGreaterThan(
            1,
            referencePath.Objects.Count,
            string.Join(" -> ", referencePath.Objects.Select(candidate => $"0x{candidate.Address:x}:{candidate.Type.TypeName}")));

        var reopened = await diagnostics.OpenSnapshotAsync(
            layout.GetFinalDumpPath(snapshot.Id),
            CancellationToken.None);
        var reopenedAnalysis = await provider.GetRequiredService<IMemorySnapshotAnalysisService>()
            .AnalyzeAsync(reopened, CancellationToken.None);
        Assert.AreEqual(MemorySnapshotOrigin.Imported, reopened.Origin);
        Assert.AreEqual(MemorySnapshotState.Ready, reopenedAnalysis.Snapshot.State);
        Assert.AreNotEqual(0, reopenedAnalysis.Types.Count);

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
