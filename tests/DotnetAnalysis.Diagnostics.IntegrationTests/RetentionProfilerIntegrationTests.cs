using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

/// <summary>
/// 验证原生保留 Profiler 可以附加真实 CoreCLR 目标并返回受控共享内存记录。
/// </summary>
[TestClass]
[DoNotParallelize]
[SuppressMessage("Naming", "CA1707:Identifiers should not have incorrect suffix", Justification = "测试名描述诊断行为。")]
public sealed class RetentionProfilerIntegrationTests
{
    /// <summary>
    /// 本项目声称支持且集成环境必须实际验证的 CoreCLR 目标框架集合。
    /// </summary>
    private static readonly string[] s_supportedTargetFrameworks = ["net8.0", "net9.0", "net10.0"];

    /// <summary>
    /// 复用性能证据的 JSON 序列化选项，避免每次基准运行额外创建配置对象。
    /// </summary>
    private static readonly JsonSerializerOptions s_indentedJsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// .NET 8、9、10 真实附加必须产生对象和 GC Root 原始记录；该测试不把 FunctionID 当作函数名。
    /// </summary>
    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    public async Task AttachAsync_ToNet8ThroughNet10Targets_CollectsObjectsAndRootsThroughSharedMemory()
    {
        var artifacts = ProfilerNativeArtifactLocator.Resolve();
        var targetFrameworks = IntegrationTestHost.GetSupportedTargetFrameworks().ToArray();
        CollectionAssert.AreEquivalent(s_supportedTargetFrameworks, targetFrameworks);
        foreach (var targetFramework in targetFrameworks)
        {
            await using var target = await IntegrationTestHost.StartTargetAsync(targetFramework, initialObjectCount: 1_000);
            using var capture = new RetentionProfilerCaptureSession();
            var result = await ProfilerControllerClient.AttachAsync(
                target.ProcessId,
                TimeSpan.FromSeconds(15),
                artifacts.ControllerPath,
                artifacts.ProfilerPath,
                capture.CreateAttachData(),
                CancellationToken.None);

            Assert.IsGreaterThanOrEqualTo(0, result, $"{targetFramework} AttachProfiler failed with HRESULT 0x{result:x8}.");
            var raw = await capture.WaitForCompletionAsync(CancellationToken.None);
            Assert.IsGreaterThan(0, raw.Objects.Count, targetFramework);
            Assert.IsGreaterThan(0, raw.Roots.Count, targetFramework);
        }
    }

    /// <summary>
    /// CLR 标记为栈根时，Profiler 必须返回经 Metadata 验证的实际持有函数，而非由诊断层拼接的名称。
    /// </summary>
    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    public async Task AttachAsync_WhenTargetKeepsObjectOnManagedStack_ReturnsHoldingFunctionEvidence()
    {
        var artifacts = ProfilerNativeArtifactLocator.Resolve();
        foreach (var targetFramework in IntegrationTestHost.GetSupportedTargetFrameworks())
        {
            await using var target = await IntegrationTestHost.StartTargetAsync(targetFramework, initialObjectCount: 1_000);
            using var commandTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.AreEqual("STACK_READY", await target.SendCommandAsync("HOLD_STACK", commandTimeout.Token), targetFramework);
            using var capture = new RetentionProfilerCaptureSession();
            _ = await ProfilerControllerClient.AttachAsync(
                target.ProcessId,
                TimeSpan.FromSeconds(15),
                artifacts.ControllerPath,
                artifacts.ProfilerPath,
                capture.CreateAttachData(),
                CancellationToken.None);

            var raw = await capture.WaitForCompletionAsync(CancellationToken.None);
            Assert.IsTrue(raw.Functions.Any(function =>
                function.FunctionName?.Contains("HoldStackRoot", StringComparison.Ordinal) is true),
                $"{targetFramework} CLR did not return the expected stack-root holding function evidence.");
            Assert.AreEqual("RELEASED", await target.SendCommandAsync("RELEASE", commandTimeout.Token), targetFramework);
        }
    }

    /// <summary>
    /// CLR 实际报告非栈根时，诊断层不得为其伪造函数证据；具体根类别由独立转换测试覆盖，
    /// 因为不同 CoreCLR 版本和运行时状态不保证每一种根类别均会出现在一次 GC 中。
    /// </summary>
    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    public async Task AttachAsync_WhenClrReportsNonStackRoots_NeverInventsFunctionEvidence()
    {
        var artifacts = ProfilerNativeArtifactLocator.Resolve();

        await using var target = await IntegrationTestHost.StartTargetAsync("net10.0", initialObjectCount: 1_000);
        using var capture = new RetentionProfilerCaptureSession();
        _ = await ProfilerControllerClient.AttachAsync(
            target.ProcessId,
            TimeSpan.FromSeconds(15),
            artifacts.ControllerPath,
            artifacts.ProfilerPath,
            capture.CreateAttachData(),
            CancellationToken.None);

        var raw = await capture.WaitForCompletionAsync(CancellationToken.None);
        var converted = RetentionProfilerSnapshotConverter.Convert(raw);
        var nonStackRoots = converted.Roots.Where(root => root.Root.Kind is not MemoryRootKind.Stack).ToArray();
        Assert.IsGreaterThan(0, nonStackRoots.Length, "CLR did not report any non-stack GC root.");
        Assert.IsFalse(nonStackRoots.Any(root => root.Root.FunctionName is not null));
        Assert.IsFalse(nonStackRoots.Any(root => root.Root.ModuleName is not null));
    }

    /// <summary>
    /// 连续附加并由 Profiler 自行分离 50 次后，目标必须保持可运行且每次均完成共享内存捕获。
    /// </summary>
    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    public async Task AttachAsync_AfterFiftyProfilerDetachCycles_TargetRemainsRunning()
    {
        const int captureCount = 50;
        var stopwatch = Stopwatch.StartNew();
        var artifacts = ProfilerNativeArtifactLocator.Resolve();
        await using var target = await IntegrationTestHost.StartTargetAsync("net10.0", initialObjectCount: 1_000);
        for (var index = 0; index < captureCount; index++)
        {
            using var capture = new RetentionProfilerCaptureSession();
            var result = await ProfilerControllerClient.AttachAsync(
                target.ProcessId,
                TimeSpan.FromSeconds(15),
                artifacts.ControllerPath,
                artifacts.ProfilerPath,
                capture.CreateAttachData(),
                CancellationToken.None);
            Assert.IsGreaterThanOrEqualTo(0, result, $"Cycle {index} AttachProfiler failed with HRESULT 0x{result:x8}.");
            var raw = await capture.WaitForCompletionAsync(CancellationToken.None);
            Assert.IsGreaterThan(0, raw.Objects.Count, $"Cycle {index} produced no object records.");
            using var commandTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Assert.AreEqual("COLLECTED", await target.SendCommandAsync("COLLECT", commandTimeout.Token));
            await capture.WaitForDetachAsync(commandTimeout.Token);
        }

        using var process = Process.GetProcessById(target.ProcessId);
        Assert.IsFalse(process.HasExited, "Target exited during repeated profiler attach/detach cycles.");
        Assert.IsLessThan(
            TimeSpan.FromMinutes(2),
            stopwatch.Elapsed,
            "Profiler detach must not synchronously block each GC callback for the CLR detach timeout.");
    }

    /// <summary>
    /// 经 Application 会话边界请求 RetentionAnalysis 时，必须保存专用快照并返回栈根的实际持有函数。
    /// </summary>
    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    public async Task CaptureSnapshotAsync_WithRetentionAnalysis_PersistsVerifiedStackRootFunction()
    {
        var snapshotRoot = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.RetentionProfilerIntegration", Guid.NewGuid().ToString("N"));
        try
        {
            await using var target = await IntegrationTestHost.StartTargetAsync("net10.0", initialObjectCount: 1_000);
            using var commandTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            Assert.AreEqual("STACK_READY", await target.SendCommandAsync("HOLD_STACK", commandTimeout.Token));
            var layout = new SnapshotStorageLayout(snapshotRoot);
            var diagnostics = new WindowsProcessDiagnostics(
                new InProcessEventBus(NullLogger<InProcessEventBus>.Instance),
                TimeProvider.System,
                snapshotLayout: layout);
            var process = (await diagnostics.GetProcessesAsync(commandTimeout.Token))
                .Single(candidate => candidate.ProcessId == target.ProcessId);
            await using var session = await diagnostics.AttachAsync(process, commandTimeout.Token);

            var snapshot = await session.CaptureSnapshotAsync(
                MemorySnapshotCaptureMode.RetentionAnalysis,
                commandTimeout.Token);

            var snapshotPath = layout.GetFinalRetentionHeapPath(snapshot.Id);
            Assert.IsTrue(File.Exists(snapshotPath), "RetentionAnalysis must persist its dedicated retentionheap snapshot.");
            var index = await RetentionHeapSnapshot.ReadIndexAsync(snapshotPath, commandTimeout.Token);
            TypeIdentity? stackType = null;
            foreach (var summary in index.TypeSummaries)
            {
                if (summary.Type.TypeName.Contains("StackHeldObject", StringComparison.Ordinal))
                {
                    stackType = summary.Type;
                    break;
                }
            }

            Assert.IsNotNull(stackType, "Profiler snapshot did not contain the controlled stack-held object type.");
            var stackObjects = index.GetObjects(stackType);
            Assert.IsGreaterThan(0, stackObjects.Count);
            var stackObject = stackObjects[0];
            Assert.IsGreaterThan(0L, stackObject.SizeBytes, "Profiler snapshot must preserve CLR-reported object sizes.");
            var paths = index.GetRetentionPaths(stackObject.Address, 16);
            Assert.IsNotNull(paths);
            Assert.IsTrue(paths.Paths.Any(path =>
                path.Root.Kind is MemoryRootKind.Stack
                && path.Root.FunctionName?.Contains("HoldStackRoot", StringComparison.Ordinal) is true));
            Assert.AreEqual("RELEASED", await target.SendCommandAsync("RELEASE", commandTimeout.Token));
        }
        finally
        {
            if (Directory.Exists(snapshotRoot))
            {
                Directory.Delete(snapshotRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// 显式百万对象压力基准必须验证 RetentionAnalysis 捕获、专用文件、分页和最多 16 条保留路径查询，
    /// 并记录耗时、工作集、快照大小与查询分配，防止只用 GCDump 基准替代 Profiler 路径验证。
    /// </summary>
    [TestMethod]
    [TestCategory("MemorySnapshotPerformance")]
    public async Task RetentionAnalysisLargeSnapshotBenchmark_RecordsCaptureAndRetentionQueryMeasurements()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("DOTNET_ANALYSIS_RUN_RETENTION_ANALYSIS_BENCHMARK"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive("Set DOTNET_ANALYSIS_RUN_RETENTION_ANALYSIS_BENCHMARK=true to run the RetentionAnalysis million-object benchmark.");
        }

        const int objectCount = 1_000_000;
        var snapshotRoot = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.RetentionAnalysisBenchmark", Guid.NewGuid().ToString("N"));
        var evidenceRoot = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.RetentionAnalysisBenchmarkEvidence");
        Directory.CreateDirectory(evidenceRoot);
        try
        {
            await using var target = await IntegrationTestHost.StartTargetAsync("net10.0", initialObjectCount: objectCount);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var layout = new SnapshotStorageLayout(snapshotRoot);
            var diagnostics = new WindowsProcessDiagnostics(
                new InProcessEventBus(NullLogger<InProcessEventBus>.Instance),
                TimeProvider.System,
                snapshotLayout: layout);
            var process = (await diagnostics.GetProcessesAsync(timeout.Token))
                .Single(candidate => candidate.ProcessId == target.ProcessId);
            await using var session = await diagnostics.AttachAsync(process, timeout.Token);

            var captureStopwatch = Stopwatch.StartNew();
            var snapshot = await session.CaptureSnapshotAsync(MemorySnapshotCaptureMode.RetentionAnalysis, timeout.Token);
            captureStopwatch.Stop();
            var snapshotPath = layout.GetFinalRetentionHeapPath(snapshot.Id);
            var snapshotBytes = new FileInfo(snapshotPath).Length;
            var index = await RetentionHeapSnapshot.ReadIndexAsync(snapshotPath, timeout.Token);
            var capturedObjectCount = index.TypeSummaries.Sum(summary => summary.ObjectCount);
            Assert.IsGreaterThanOrEqualTo(
                objectCount,
                capturedObjectCount,
                "RetentionAnalysis capture did not preserve the million-object target workload.");
            var largestType = index.TypeSummaries.OrderByDescending(summary => summary.ObjectCount).First().Type;
            var page = index.GetPage(largestType, 0, 1_000);
            Assert.HasCount(1_000, page.Objects);

            _ = index.GetRetentionPaths(page.Objects[0].Address, 16);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var beforeQuery = GC.GetTotalAllocatedBytes(precise: true);
            var paths = index.GetRetentionPaths(page.Objects[0].Address, 16);
            var queryAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - beforeQuery;

            Assert.IsNotNull(paths);
            Assert.IsLessThanOrEqualTo(16, paths.Paths.Count);
            Assert.IsLessThan(TimeSpan.FromMinutes(2), captureStopwatch.Elapsed);
            Assert.IsLessThan(RetentionSnapshotStorageGuard.MaximumSnapshotBytes, snapshotBytes);
            Assert.IsLessThan(64L * 1024 * 1024, queryAllocatedBytes);

            using var targetProcess = Process.GetProcessById(target.ProcessId);
            var evidence = new
            {
                objectCount,
                capturedObjectCount,
                captureMilliseconds = captureStopwatch.Elapsed.TotalMilliseconds,
                snapshotBytes,
                targetWorkingSetBytes = targetProcess.WorkingSet64,
                runnerPrivateMemoryBytes = Process.GetCurrentProcess().PrivateMemorySize64,
                cachedRetentionQueryAllocatedBytes = queryAllocatedBytes,
                retentionPathCount = paths.Paths.Count
            };
            var evidencePath = Path.Combine(
                evidenceRoot,
                $"RetentionAnalysis-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.json");
            await File.WriteAllTextAsync(
                evidencePath,
                JsonSerializer.Serialize(evidence, s_indentedJsonOptions),
                timeout.Token);
        }
        finally
        {
            if (Directory.Exists(snapshotRoot))
            {
                Directory.Delete(snapshotRoot, recursive: true);
            }
        }
    }
}
