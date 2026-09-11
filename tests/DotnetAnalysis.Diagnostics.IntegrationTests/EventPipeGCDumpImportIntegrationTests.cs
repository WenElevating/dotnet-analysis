using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text.Json;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

/// <summary>
/// 验证由真实运行时产生的原始 EventPipe .gcdump 可经流式 spool 导入，而不依赖手工伪造协议 fixture。
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class EventPipeGCDumpImportIntegrationTests
{
    private const string BenchmarkGate = "DOTNET_ANALYSIS_RUN_LARGE_SNAPSHOT_BENCHMARK";
    private const int LargeTargetObjectCount = 1_000_000;
    private const long MaximumPeakManagedIncreaseBytes = 384L * 1024 * 1024;
    private const long MaximumRetainedManagedIncreaseBytes = 128L * 1024 * 1024;
    private const long MaximumPeakPrivateIncreaseBytes = 1024L * 1024 * 1024;
    private readonly TestContext _testContext;

    /// <summary>
    /// 初始化 MSTest 提供的协作取消上下文。
    /// </summary>
    /// <param name="testContext">当前测试运行上下文。</param>
    public EventPipeGCDumpImportIntegrationTests(TestContext testContext)
    {
        _testContext = testContext ?? throw new ArgumentNullException(nameof(testContext));
    }

    /// <summary>
    /// 真实 EventPipe 文件应绕过 FastSerialization 解析，发布可分页、可追踪且只含 Unknown 根证据的映射索引。
    /// </summary>
    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task RuntimeEventPipeGCDumpImportsThroughStreamingMappedIndex()
    {
        if (!IntegrationTestHost.GetInstalledRuntimeMajorVersions().Contains(10))
        {
            Assert.Inconclusive("net10 runtime is required for the raw EventPipe gcdump import test.");
        }

        await using var target = await IntegrationTestHost.StartTargetAsync("net10.0", initialObjectCount: 10_000);
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.RawEventPipeImport.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var rawPath = Path.Combine(root, "runtime-raw.gcdump");
            await CaptureRawEventPipeGCDumpAsync(target.ProcessId, rawPath, _testContext.CancellationToken);
            var layout = new SnapshotStorageLayout(Path.Combine(root, "snapshots"));
            var snapshotId = MemorySnapshotId.New();
            var router = new HeapIndexRouter(layout, maximumInMemoryObjectCount: 1, maximumInMemoryBytes: 1);
            var reader = (IIndexedMemorySnapshotReader)new GCDumpSnapshotReader();

            var compatibilitySummaries = await ((IMemorySnapshotReader)reader)
                .ReadTypeSummariesAsync(rawPath, _testContext.CancellationToken);
            Assert.IsGreaterThan(0, compatibilitySummaries.Count);

            using var handle = await reader.BuildIndexAsync(
                snapshotId,
                rawPath,
                router,
                _testContext.CancellationToken);

            Assert.IsTrue(handle.IsMapped);
            Assert.IsFalse(handle.IsInMemoryGraphResident);
            Assert.IsGreaterThan(0, handle.TypeSummaries.Count);
            var path = FindRootedPath(handle, _testContext.CancellationToken);
            Assert.IsNotNull(path, "The runtime EventPipe heap should expose at least one rooted object path.");
            Assert.AreEqual(MemoryRootKind.Unknown, path.Paths[0].Root.Kind);
            Assert.IsNull(path.Paths[0].Root.FunctionName);
            Assert.IsNull(path.Paths[0].Root.ModuleName);
            Assert.IsTrue(File.Exists(rawPath), "Index publication must preserve the imported raw gcdump evidence.");
            Assert.IsEmpty(
                Directory.GetDirectories(layout.GetSnapshotDirectory(snapshotId), ".eventpipe-heap-spool.*.tmp"),
                "The call-specific EventPipe spool must be removed after a successful import.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 真实百万对象 EventPipe 堆必须由默认资源策略自动路由到磁盘索引；从原始采集到分页和根路径查询的全过程
    /// 不得保留整图托管集合，也不得遗留 spool 或发布临时目录，峰值门禁必须能识别整图重新物化回归。
    /// </summary>
    [TestMethod]
    [TestCategory("MemorySnapshotPerformance")]
    [Timeout(600_000, CooperativeCancellation = true)]
    public async Task LargeRuntimeEventPipeGCDumpUsesDefaultMappedRouteWithinMemoryBudget()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(BenchmarkGate), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive($"Set {BenchmarkGate}=true to run the real EventPipe large-index pressure test.");
        }

        if (!IntegrationTestHost.GetInstalledRuntimeMajorVersions().Contains(10))
        {
            Assert.Inconclusive("net10 runtime is required for the real EventPipe large-index pressure test.");
        }

        await using var target = await IntegrationTestHost.StartTargetAsync("net10.0", LargeTargetObjectCount);
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.RawEventPipePressure.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var rawPath = Path.Combine(root, "runtime-large-raw.gcdump");
            var capture = Stopwatch.StartNew();
            await CaptureRawEventPipeGCDumpAsync(target.ProcessId, rawPath, _testContext.CancellationToken);
            capture.Stop();
            var rawLength = new FileInfo(rawPath).Length;
            var layout = new SnapshotStorageLayout(Path.Combine(root, "snapshots"));
            var snapshotId = MemorySnapshotId.New();
            var reader = (IIndexedMemorySnapshotReader)new GCDumpSnapshotReader();
            var router = new HeapIndexRouter(layout);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var baseline = ReadProcessMemorySample();
            using var samplerCancellation = new CancellationTokenSource();
            var sampling = SampleProcessMemoryUntilCanceledAsync(baseline, samplerCancellation.Token);
            HeapIndexHandle handle;
            ProcessMemorySample peak;
            var build = Stopwatch.StartNew();
            try
            {
                handle = await reader.BuildIndexAsync(
                    snapshotId,
                    rawPath,
                    router,
                    _testContext.CancellationToken);
            }
            finally
            {
                build.Stop();
                samplerCancellation.Cancel();
                peak = await sampling;
            }

            using (handle)
            {
                var actualObjectCount = handle.TypeSummaries.Sum(static summary => summary.ObjectCount);
                Assert.IsGreaterThanOrEqualTo(LargeTargetObjectCount, actualObjectCount);
                Assert.IsTrue(handle.IsMapped);
                Assert.IsFalse(handle.IsInMemoryGraphResident);
                Assert.AreEqual(MemorySnapshotObjectAccessMode.Paged, handle.ObjectAccessMode);

                var largestType = handle.TypeSummaries.MaxBy(static summary => summary.ObjectCount)
                    ?? throw new AssertFailedException("The real EventPipe heap did not contain a type summary.");
                var enumerationFailure = Assert.ThrowsExactly<DiagnosticsException>(() => handle.GetObjects(largestType.Type));
                Assert.AreEqual(DiagnosticsErrorCode.SnapshotTooLargeForFullEnumeration, enumerationFailure.ErrorCode);
                var page = handle.GetPage(largestType.Type, 0, checked((int)Math.Min(1_000, largestType.ObjectCount)));
                Assert.IsGreaterThan(0, page.Objects.Count);
                Assert.AreEqual(largestType.ObjectCount, page.TotalObjectCount);
                Assert.IsNotNull(
                    FindRootedPath(handle, _testContext.CancellationToken),
                    "The retained target allocations should expose at least one GC-root path.");

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var retained = ReadProcessMemorySample();
                var measurement = new RuntimeEventPipeIndexMeasurement(
                    LargeTargetObjectCount,
                    actualObjectCount,
                    rawLength,
                    capture.Elapsed.TotalMilliseconds,
                    build.Elapsed.TotalMilliseconds,
                    baseline,
                    peak,
                    retained,
                    GetDirectorySize(layout.GetHeapIndexDirectory(snapshotId)));
                _testContext.WriteLine(JsonSerializer.Serialize(measurement));

                Assert.IsLessThan(
                    MaximumPeakManagedIncreaseBytes,
                    Math.Max(0, peak.ManagedHeapBytes - baseline.ManagedHeapBytes),
                    "Real EventPipe parsing and mapped-index construction exceeded the managed-memory peak budget.");
                Assert.IsLessThan(
                    MaximumRetainedManagedIncreaseBytes,
                    Math.Max(0, retained.ManagedHeapBytes - baseline.ManagedHeapBytes),
                    "The mapped index retained a managed graph after construction.");
                Assert.IsLessThan(
                    MaximumPeakPrivateIncreaseBytes,
                    Math.Max(0, peak.PrivateMemoryBytes - baseline.PrivateMemoryBytes),
                    "Real EventPipe parsing and mapped-index construction exceeded the diagnostics-process private-memory budget.");
            }

            Assert.IsTrue(File.Exists(rawPath));
            Assert.AreEqual(rawLength, new FileInfo(rawPath).Length);
            var snapshotDirectory = layout.GetSnapshotDirectory(snapshotId);
            Assert.IsEmpty(
                Directory.EnumerateDirectories(snapshotDirectory, "*.tmp", SearchOption.AllDirectories).ToArray(),
                "A successful real EventPipe build must not leave spool, external-sort, or publication temporary directories.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 在不构建托管图的情况下消费实时 EventPipe 流，同时逐字节复制为可重复导入的原始文件。
    /// </summary>
    private static async Task CaptureRawEventPipeGCDumpAsync(
        int processId,
        string outputPath,
        CancellationToken cancellationToken)
    {
        using var session = new DiagnosticsClient(processId).StartEventPipeSession(
            new EventPipeProvider(
                "Microsoft-Windows-DotNETRuntime",
                EventLevel.Verbose,
                (long)ClrTraceEventParser.Keywords.GCHeapSnapshot),
            requestRundown: true,
            circularBufferMB: 128);
        await using var output = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var copying = session.EventStream.CopyToAsync(output, cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        StopSession(session);
        await copying.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// 在真实根地址不固定的快照中有界寻找一条可验证路径，避免测试依赖进程地址或特定类型排序。
    /// </summary>
    private static MemoryRetentionPathResult? FindRootedPath(
        HeapIndexHandle handle,
        CancellationToken cancellationToken)
    {
        foreach (var summary in handle.TypeSummaries.Take(64))
        {
            var pageSize = checked((int)Math.Min(summary.ObjectCount, 32));
            if (pageSize == 0)
            {
                continue;
            }

            foreach (var item in handle.GetPage(summary.Type, 0, pageSize).Objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = handle.GetRetentionPaths(item.Address, 1, cancellationToken);
                if (path is not null)
                {
                    return path;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 在索引构建期间持续采样当前诊断进程内存；取消只终止采样循环，并在返回前补采阶段尾部。
    /// </summary>
    /// <param name="initial">构建开始前的基线样本。</param>
    /// <param name="cancellationToken">构建完成后用于停止采样的令牌。</param>
    /// <returns>各项指标在采样区间内的峰值。</returns>
    private static async Task<ProcessMemorySample> SampleProcessMemoryUntilCanceledAsync(
        ProcessMemorySample initial,
        CancellationToken cancellationToken)
    {
        var peak = initial;
        using var process = Process.GetCurrentProcess();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                peak = ReadProcessMemorySample(process, peak);
                await Task.Delay(10, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ReadProcessMemorySample(process, peak);
        }
    }

    /// <summary>
    /// 读取当前测试进程的私有内存、工作集和托管堆瞬时值。
    /// </summary>
    /// <returns>用于构建前基线或完整 GC 后保留量判断的进程内存样本。</returns>
    private static ProcessMemorySample ReadProcessMemorySample()
    {
        using var process = Process.GetCurrentProcess();
        return ReadProcessMemorySample(process, default);
    }

    /// <summary>
    /// 刷新进程计数器并与既有峰值逐项合并，避免短阶段的后续较小样本覆盖峰值。
    /// </summary>
    /// <param name="process">当前测试进程句柄。</param>
    /// <param name="peak">此前已观测峰值；默认值表示读取单个瞬时样本。</param>
    /// <returns>合并后的逐项最大值。</returns>
    private static ProcessMemorySample ReadProcessMemorySample(Process process, ProcessMemorySample peak)
    {
        process.Refresh();
        return new ProcessMemorySample(
            Math.Max(peak.PrivateMemoryBytes, process.PrivateMemorySize64),
            Math.Max(peak.WorkingSetBytes, process.WorkingSet64),
            Math.Max(peak.ManagedHeapBytes, GC.GetTotalMemory(forceFullCollection: false)));
    }

    /// <summary>
    /// 顺序累计已发布索引工件大小，只用于压力测试证据输出。
    /// </summary>
    /// <param name="directory">已发布且完成校验的 .heapidx 目录。</param>
    /// <returns>目录中全部文件的总字节数。</returns>
    private static long GetDirectorySize(string directory) => Directory
        .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Sum(static path => new FileInfo(path).Length);

    /// <summary>
    /// 请求停止实时会话，使原始 EventPipe 文件获得完整结束记录；停止竞态不覆盖此前完整性判断。
    /// </summary>
    private static void StopSession(EventPipeSession session)
    {
        try
        {
            session.Stop();
        }
        catch (Exception exception) when (exception is DiagnosticsClientException or IOException or InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// 表示一次诊断进程内存采样；各字段可作为瞬时值或逐项峰值使用。
    /// </summary>
    /// <param name="PrivateMemoryBytes">私有内存字节数。</param>
    /// <param name="WorkingSetBytes">工作集字节数。</param>
    /// <param name="ManagedHeapBytes">托管堆字节数。</param>
    private readonly record struct ProcessMemorySample(
        long PrivateMemoryBytes,
        long WorkingSetBytes,
        long ManagedHeapBytes);

    /// <summary>
    /// 保存真实 EventPipe 捕获、默认路由索引构建及内存边界的可复现测量值。
    /// </summary>
    /// <param name="RequestedTargetObjectCount">目标进程启动时请求保留的数组数量。</param>
    /// <param name="ActualHeapObjectCount">EventPipe 实际报告并进入索引的总对象数。</param>
    /// <param name="RawSnapshotBytes">原始 EventPipe 文件大小。</param>
    /// <param name="CaptureMilliseconds">原始采集耗时。</param>
    /// <param name="IndexBuildMilliseconds">生产读取器和默认路由的索引耗时。</param>
    /// <param name="Baseline">索引构建前的进程内存基线。</param>
    /// <param name="Peak">索引构建期间的逐项峰值。</param>
    /// <param name="Retained">索引完成并执行完整 GC 后的保留量。</param>
    /// <param name="IndexBytes">最终 .heapidx 工件体积。</param>
    private sealed record RuntimeEventPipeIndexMeasurement(
        int RequestedTargetObjectCount,
        long ActualHeapObjectCount,
        long RawSnapshotBytes,
        double CaptureMilliseconds,
        double IndexBuildMilliseconds,
        ProcessMemorySample Baseline,
        ProcessMemorySample Peak,
        ProcessMemorySample Retained,
        long IndexBytes);

}
