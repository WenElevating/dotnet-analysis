using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

/// <summary>
/// 对 EventPipe 固定宽度 spool 与外排映射路径执行显式百万对象压力验证。
/// </summary>
[TestClass]
[DoNotParallelize]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "测试名称描述行为。")]
public sealed class EventPipeHeapSpoolStressTests
{
    private const string BenchmarkGate = "DOTNET_ANALYSIS_RUN_LARGE_SNAPSHOT_BENCHMARK";
    private const int ObjectCount = 1_000_000;
    private readonly TestContext _testContext;

    /// <summary>
    /// 初始化 MSTest 提供的协作取消与结果输出上下文。
    /// </summary>
    /// <param name="testContext">当前测试运行上下文。</param>
    public EventPipeHeapSpoolStressTests(TestContext testContext)
    {
        _testContext = testContext ?? throw new ArgumentNullException(nameof(testContext));
    }

    /// <summary>
    /// 百万 EventPipe 对象链必须直接发布映射工件，并在完整 GC 后不留下与全图等量的托管对象、地址或边集合。
    /// </summary>
    [TestMethod]
    [TestCategory("MemorySnapshotPerformance")]
    [Timeout(600_000, CooperativeCancellation = true)]
    public async Task MillionObjectEventPipeSpool_PublishesMappedIndexWithoutResidentManagedGraph()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(BenchmarkGate), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive($"Set {BenchmarkGate}=true to run the million-object EventPipe spool stress test.");
        }

        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.EventPipeSpoolStress.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var managedBeforeBytes = GC.GetTotalMemory(forceFullCollection: true);
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var privateBeforeBytes = process.PrivateMemorySize64;
            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            var generation = Stopwatch.StartNew();
            using var spool = await EventPipeHeapSpool.CreateAsync(
                layout.GetSnapshotDirectory(snapshotId),
                _testContext.CancellationToken);
            spool.RecordType(1, "Stress.EventPipeNode", "DotnetAnalysis.Stress");
            for (var index = 1; index <= ObjectCount; index++)
            {
                _testContext.CancellationToken.ThrowIfCancellationRequested();
                spool.RecordNode((ulong)index, 1, 32, index == ObjectCount ? 0 : 1);
                if (index != ObjectCount)
                {
                    spool.RecordEdgeTarget((ulong)(index + 1));
                }
            }

            spool.RecordRoot(1);
            generation.Stop();
            var indexBuild = Stopwatch.StartNew();
            var router = new HeapIndexRouter(layout);
            using var handle = await GCDumpSnapshotReader.RouteEventPipeSpoolAsync(
                snapshotId,
                spool,
                sourceLengthBytes: 0,
                router,
                _testContext.CancellationToken);
            indexBuild.Stop();

            var type = new TypeIdentity("Stress.EventPipeNode", "DotnetAnalysis.Stress");
            Assert.IsTrue(handle.IsMapped);
            Assert.IsFalse(handle.IsInMemoryGraphResident);
            Assert.AreEqual(ObjectCount, handle.TypeSummaries.Single().ObjectCount);
            var fullEnumeration = Assert.ThrowsExactly<DiagnosticsException>(() => handle.GetObjects(type));
            Assert.AreEqual(DiagnosticsErrorCode.SnapshotTooLargeForFullEnumeration, fullEnumeration.ErrorCode);
            var page = handle.GetPage(type, ObjectCount - 1_000, 1_000);
            Assert.HasCount(1_000, page.Objects);
            Assert.AreEqual((ulong)ObjectCount, page.Objects[^1].Address);
            var path = handle.GetReferencePath(1_000, _testContext.CancellationToken);
            Assert.IsNotNull(path);
            Assert.AreEqual(1UL, path.Objects[0].Address);
            Assert.AreEqual(1_000UL, path.Objects[^1].Address);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var managedAfterBytes = GC.GetTotalMemory(forceFullCollection: true);
            process.Refresh();
            var measurement = new EventPipeSpoolStressMeasurement(
                ObjectCount,
                ObjectCount - 1L,
                generation.Elapsed.TotalMilliseconds,
                indexBuild.Elapsed.TotalMilliseconds,
                managedBeforeBytes,
                managedAfterBytes,
                privateBeforeBytes,
                process.PrivateMemorySize64,
                GetDirectorySize(layout.GetHeapIndexDirectory(snapshotId)));
            _testContext.WriteLine(JsonSerializer.Serialize(measurement));
            Assert.IsLessThan(
                64L * 1024 * 1024,
                Math.Max(0, managedAfterBytes - managedBeforeBytes),
                "The mapped EventPipe index retained managed memory proportional to the million-object graph.");
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
    /// 顺序累计已发布工件目录体积，仅用于压力证据输出。
    /// </summary>
    private static long GetDirectorySize(string directory) => Directory
        .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Sum(static path => new FileInfo(path).Length);

    /// <summary>
    /// 保存百万对象 EventPipe spool 的生成、索引、托管保留与磁盘体积测量。
    /// </summary>
    private sealed record EventPipeSpoolStressMeasurement(
        int ObjectCount,
        long EdgeCount,
        double GenerationMilliseconds,
        double IndexBuildMilliseconds,
        long ManagedBeforeBytes,
        long ManagedAfterBytes,
        long PrivateBeforeBytes,
        long PrivateAfterBytes,
        long IndexBytes);
}
