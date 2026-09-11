using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

/// <summary>
/// 对大对象图的外排索引路径执行显式压力验证，覆盖 5M 与 10M 对象及高边密度，
/// 避免将小型真实捕获误当作磁盘索引容量证明。
/// </summary>
[TestClass]
[DoNotParallelize]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "测试名称描述行为。")]
public sealed class LargeHeapIndexStressTests
{
    private const string StressGate = "DOTNET_ANALYSIS_RUN_HEAP_INDEX_STRESS";
    private const string StressObjectCounts = "DOTNET_ANALYSIS_HEAP_INDEX_STRESS_OBJECT_COUNTS";
    private static readonly int[] s_objectCounts = [5_000_000, 10_000_000];
    private static readonly JsonSerializerOptions s_indentedJsonOptions = new() { WriteIndented = true };
    private readonly TestContext _testContext;

    /// <summary>
    /// 初始化 MSTest 提供的协作取消上下文。
    /// </summary>
    /// <param name="testContext">当前测试运行上下文。</param>
    public LargeHeapIndexStressTests(TestContext testContext)
    {
        _testContext = testContext ?? throw new ArgumentNullException(nameof(testContext));
    }

    /// <summary>
    /// 5M 与 10M 高边密度 raw spool 必须在不创建完整对象或边 DTO 集合的前提下发布映射索引，
    /// 并维持分页契约和受控的诊断进程私有内存峰值。
    /// </summary>
    [TestMethod]
    [TestCategory("MemorySnapshotPerformance")]
    [Timeout(1_800_000, CooperativeCancellation = true)]
    public async Task HeapIndexStress_5MAnd10MHighDensityGraphs_PublishQueryableMappedArtifacts()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(StressGate), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive($"Set {StressGate}=true to run the 5M and 10M heap-index stress test.");
        }

        foreach (var objectCount in GetConfiguredObjectCounts())
        {
            var measurement = await RunCaseAsync(objectCount, _testContext.CancellationToken);
            _testContext.WriteLine(JsonSerializer.Serialize(measurement, s_indentedJsonOptions));
        }
    }

    /// <summary>
    /// 运行一个给定对象数量的链加跨步边图，并写出便于性能回归比对的原始测量结果。
    /// </summary>
    /// <param name="objectCount">要流式生成的对象数。</param>
    /// <param name="cancellationToken">取消当前生成、索引或查询的令牌。</param>
    /// <returns>表示完整压力场景的任务。</returns>
    private static async Task<HeapIndexStressMeasurement> RunCaseAsync(int objectCount, CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.HeapIndexStress.{objectCount}.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var layout = new SnapshotStorageLayout(root);
            var snapshotId = MemorySnapshotId.New();
            var retentionPath = Path.Combine(root, "synthetic.retentionheap");
            var process = Process.GetCurrentProcess();
            var privateMemoryBeforeBytes = process.PrivateMemorySize64;
            long privateMemoryPeakBytes = privateMemoryBeforeBytes;
            var generation = Stopwatch.StartNew();
            using var spool = await RetentionProfilerRawCaptureSpool.CreateEmptyAsync(root, cancellationToken);
            spool.SetEvidence(
                Array.Empty<RetentionProfilerRawFunction>(),
                [new RetentionProfilerRawType((nuint)1, "Stress.DenseNode", "DotnetAnalysis.Stress")]);
            await WriteHighDensityGraphAsync(spool, objectCount, cancellationToken);
            generation.Stop();
            privateMemoryPeakBytes = Math.Max(privateMemoryPeakBytes, process.PrivateMemorySize64);

            var retentionWrite = Stopwatch.StartNew();
            await RetentionHeapSnapshot.WriteFromProfilerSpoolAsync(retentionPath, spool, cancellationToken);
            retentionWrite.Stop();
            privateMemoryPeakBytes = Math.Max(privateMemoryPeakBytes, process.PrivateMemorySize64);

            var indexWrite = Stopwatch.StartNew();
            var reader = (IIndexedMemorySnapshotReader)new RetentionHeapSnapshotReader();
            var router = new HeapIndexRouter(layout);
            using var handle = await reader.BuildIndexAsync(snapshotId, retentionPath, router, cancellationToken);
            indexWrite.Stop();
            privateMemoryPeakBytes = Math.Max(privateMemoryPeakBytes, process.PrivateMemorySize64);

            var type = new TypeIdentity("Stress.DenseNode", "DotnetAnalysis.Stress");
            var page = handle.GetPage(type, objectCount - 1_000, 1_000);
            Assert.IsTrue(handle.IsMapped);
            Assert.AreEqual((long)objectCount, page.TotalObjectCount);
            Assert.HasCount(1_000, page.Objects);
            Assert.AreEqual((ulong)objectCount, page.Objects[^1].Address);
            var retentionPaths = handle.GetRetentionPaths(1_000, 1, cancellationToken);
            Assert.IsNotNull(retentionPaths);
            Assert.AreEqual(1UL, retentionPaths.Paths[0].Objects[0].Address);
            Assert.AreEqual(1_000UL, retentionPaths.Paths[0].Objects[^1].Address);
            var dominatorMeasurement = await InvokeDominatorQueryAsync(handle, objectCount, cancellationToken);
            privateMemoryPeakBytes = Math.Max(privateMemoryPeakBytes, dominatorMeasurement.PrivateMemoryPeakBytes);
            Assert.IsLessThan(
                2L * 1024 * 1024 * 1024,
                privateMemoryPeakBytes - privateMemoryBeforeBytes,
                "Large disk-index construction exceeded the 8 GiB workstation diagnostics-process memory budget.");

            var measurement = new HeapIndexStressMeasurement(
                objectCount,
                checked((long)objectCount * 2 - 3),
                generation.Elapsed.TotalMilliseconds,
                retentionWrite.Elapsed.TotalMilliseconds,
                indexWrite.Elapsed.TotalMilliseconds,
                privateMemoryBeforeBytes,
                privateMemoryPeakBytes,
                dominatorMeasurement,
                new FileInfo(retentionPath).Length,
                GetDirectorySize(layout.GetHeapIndexDirectory(snapshotId)));
            await File.WriteAllTextAsync(
                Path.Combine(root, "heap-index-stress.json"),
                JsonSerializer.Serialize(measurement, s_indentedJsonOptions),
                cancellationToken);
            return measurement;
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
    /// 在整个派生调用期间连续采样当前进程内存，并要求生产最大预算下真实完成 Dominator 分页；
    /// 5M、10M 或通过环境变量下调的烟测规模均不得用资源拒绝分支代替成功证据。
    /// </summary>
    private static async Task<DominatorStressMeasurement> InvokeDominatorQueryAsync(
        HeapIndexHandle handle,
        int objectCount,
        CancellationToken cancellationToken)
    {
        var budgetBytes = HeapIndexResourcePolicy.GetBudgetBytes();
        Assert.AreEqual(HeapIndexResourcePolicy.MaximumBudgetBytes, budgetBytes);
        Assert.AreEqual(64L * 1024 * 1024, HeapMappedWindowBudget.MaximumLeasedBytes);
        var selectedChunkBytes = HeapIndexResourcePolicy.CalculateDominatorExternalSortChunkBytes(
            objectCount,
            budgetBytes,
            HeapIndexResourcePolicy.GetExternalSortChunkBytes());
        Assert.IsGreaterThanOrEqualTo(HeapIndexResourcePolicy.MinimumExternalSortChunkBytes, selectedChunkBytes);
        var initialSample = ReadProcessMemorySample();
        using var samplerCancellation = new CancellationTokenSource();
        var sampler = SampleProcessMemoryUntilCanceledAsync(initialSample, samplerCancellation.Token);
        MemoryDominatorPage page;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            page = await handle.GetDominatorPageAsync(0, 1_000, cancellationToken);
        }
        finally
        {
            stopwatch.Stop();
            samplerCancellation.Cancel();
        }

        var peak = await sampler;
        Assert.AreEqual((long)objectCount, page.TotalObjectCount);
        Assert.HasCount(1_000, page.Objects);
        Assert.AreEqual(1UL, page.Objects[0].ObjectInfo.Address);
        Assert.AreEqual(checked((long)objectCount * 64), page.Objects[0].RetainedSizeBytes);

        return new DominatorStressMeasurement(
            ExpectedToBuild: true,
            Built: true,
            stopwatch.Elapsed.TotalMilliseconds,
            budgetBytes,
            initialSample.PrivateMemoryBytes,
            peak.PrivateMemoryBytes,
            initialSample.WorkingSetBytes,
            peak.WorkingSetBytes,
            initialSample.ManagedHeapBytes,
            peak.ManagedHeapBytes);
    }

    /// <summary>
    /// 按固定周期读取实际进程计数器，直到派生调用完成；最终样本在取消后再读取一次以覆盖阶段尾部。
    /// </summary>
    private static async Task<ProcessMemorySample> SampleProcessMemoryUntilCanceledAsync(
        ProcessMemorySample initial,
        CancellationToken cancellationToken)
    {
        var privateMemoryPeakBytes = initial.PrivateMemoryBytes;
        var workingSetPeakBytes = initial.WorkingSetBytes;
        var managedHeapPeakBytes = initial.ManagedHeapBytes;
        using var process = Process.GetCurrentProcess();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                process.Refresh();
                privateMemoryPeakBytes = Math.Max(privateMemoryPeakBytes, process.PrivateMemorySize64);
                workingSetPeakBytes = Math.Max(workingSetPeakBytes, process.WorkingSet64);
                managedHeapPeakBytes = Math.Max(managedHeapPeakBytes, GC.GetTotalMemory(forceFullCollection: false));
                await Task.Delay(10, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            process.Refresh();
            privateMemoryPeakBytes = Math.Max(privateMemoryPeakBytes, process.PrivateMemorySize64);
            workingSetPeakBytes = Math.Max(workingSetPeakBytes, process.WorkingSet64);
            managedHeapPeakBytes = Math.Max(managedHeapPeakBytes, GC.GetTotalMemory(forceFullCollection: false));
        }

        return new ProcessMemorySample(privateMemoryPeakBytes, workingSetPeakBytes, managedHeapPeakBytes);
    }

    /// <summary>
    /// 读取当前时刻的进程私有内存、工作集和托管堆大小。
    /// </summary>
    private static ProcessMemorySample ReadProcessMemorySample()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new ProcessMemorySample(
            process.PrivateMemorySize64,
            process.WorkingSet64,
            GC.GetTotalMemory(forceFullCollection: false));
    }

    /// <summary>
    /// 解析可选的逗号分隔对象数；未配置时保留生产门禁要求的 5M 与 10M 两档。
    /// </summary>
    private static IReadOnlyList<int> GetConfiguredObjectCounts()
    {
        var configured = Environment.GetEnvironmentVariable(StressObjectCounts);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return s_objectCounts;
        }

        var values = new List<int>();
        foreach (var item in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1_000)
            {
                throw new InvalidOperationException($"{StressObjectCounts} must contain comma-separated integers greater than or equal to 1000.");
            }

            if (!values.Contains(value))
            {
                values.Add(value);
            }
        }

        return values.Count == 0
            ? throw new InvalidOperationException($"{StressObjectCounts} did not contain an object count.")
            : values;
    }

    /// <summary>
    /// 使用固定大小缓冲区直接写出对象、两类前向边和一个根，整个过程不保留图记录集合。
    /// </summary>
    /// <param name="spool">接收固定宽度 raw 记录的一次性 spool。</param>
    /// <param name="objectCount">对象数，至少为 1,000 以支持分页验证。</param>
    /// <param name="cancellationToken">取消当前顺序文件写入的令牌。</param>
    /// <returns>表示 raw 图生成完成的任务。</returns>
    private static async Task WriteHighDensityGraphAsync(
        RetentionProfilerRawCaptureSpool spool,
        int objectCount,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(objectCount, 1_000);
        await using var objects = new FileStream(spool.ObjectPath, FileMode.Append, FileAccess.Write, FileShare.None, 1_048_576, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var edges = new FileStream(spool.EdgePath, FileMode.Append, FileAccess.Write, FileShare.None, 1_048_576, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var roots = new FileStream(spool.RootPath, FileMode.Append, FileAccess.Write, FileShare.None, 4_096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var objectBuffer = new byte[1_048_560];
        var edgeBuffer = new byte[1_048_576];
        var objectBuffered = 0;
        var edgeBuffered = 0;
        for (var address = 1; address <= objectCount; address++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteObject(objectBuffer.AsSpan(objectBuffered, RetentionProfilerRawCaptureSpool.ObjectRecordBytes), (ulong)address);
            objectBuffered += RetentionProfilerRawCaptureSpool.ObjectRecordBytes;
            if (objectBuffered == objectBuffer.Length)
            {
                await objects.WriteAsync(objectBuffer, cancellationToken);
                objectBuffered = 0;
            }

            if (address < objectCount)
            {
                WriteEdge(edgeBuffer.AsSpan(edgeBuffered, RetentionProfilerRawCaptureSpool.EdgeRecordBytes), (ulong)address, (ulong)(address + 1));
                edgeBuffered += RetentionProfilerRawCaptureSpool.EdgeRecordBytes;
            }

            if (address + 2 <= objectCount)
            {
                WriteEdge(edgeBuffer.AsSpan(edgeBuffered, RetentionProfilerRawCaptureSpool.EdgeRecordBytes), (ulong)address, (ulong)(address + 2));
                edgeBuffered += RetentionProfilerRawCaptureSpool.EdgeRecordBytes;
            }

            if (edgeBuffered > edgeBuffer.Length - RetentionProfilerRawCaptureSpool.EdgeRecordBytes * 2)
            {
                await edges.WriteAsync(edgeBuffer.AsMemory(0, edgeBuffered), cancellationToken);
                edgeBuffered = 0;
            }
        }

        if (objectBuffered > 0)
        {
            await objects.WriteAsync(objectBuffer.AsMemory(0, objectBuffered), cancellationToken);
        }
        if (edgeBuffered > 0)
        {
            await edges.WriteAsync(edgeBuffer.AsMemory(0, edgeBuffered), cancellationToken);
        }

        Span<byte> root = stackalloc byte[RetentionProfilerRawCaptureSpool.RootRecordBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(root, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(root.Slice(sizeof(ulong)), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(root.Slice(sizeof(ulong) + sizeof(uint)), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(root.Slice(sizeof(ulong) + sizeof(uint) * 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(root.Slice(sizeof(ulong) * 2 + sizeof(uint) * 2), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(root.Slice(sizeof(ulong) * 2 + sizeof(uint) * 3), 0);
        await roots.WriteAsync(root.ToArray(), cancellationToken);
    }

    /// <summary>
    /// 写入固定宽度对象记录。
    /// </summary>
    /// <param name="destination">恰好容纳一个对象记录的目标缓冲区。</param>
    /// <param name="address">对象地址。</param>
    private static void WriteObject(Span<byte> destination, ulong address)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, address);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(sizeof(ulong)), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(sizeof(ulong) * 2), 64);
    }

    /// <summary>
    /// 写入固定宽度引用边记录。
    /// </summary>
    /// <param name="destination">恰好容纳一条边记录的目标缓冲区。</param>
    /// <param name="source">源对象地址。</param>
    /// <param name="target">目标对象地址。</param>
    private static void WriteEdge(Span<byte> destination, ulong source, ulong target)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, source);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(sizeof(ulong)), target);
    }

    /// <summary>
    /// 顺序统计已发布工件目录的总大小，用于压力证据而非容量决策。
    /// </summary>
    /// <param name="directory">已发布的 .heapidx 目录。</param>
    /// <returns>目录内所有文件的总字节数。</returns>
    private static long GetDirectorySize(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Sum(path => new FileInfo(path).Length);

    /// <summary>
    /// 保存单个大图压力场景的时延、内存和工件体积测量；既写入临时 JSON 便于本地故障诊断，
    /// 也通过 <see cref="TestContext.WriteLine(string)"/> 写入测试结果，避免临时目录清理后丢失性能证据。
    /// </summary>
    private sealed record HeapIndexStressMeasurement(
        int ObjectCount,
        long EdgeCount,
        double GenerationMilliseconds,
        double RetentionWriteMilliseconds,
        double IndexWriteMilliseconds,
        long PrivateMemoryBeforeBytes,
        long PrivateMemoryPeakBytes,
        DominatorStressMeasurement Dominator,
        long RetentionBytes,
        long IndexBytes);

    /// <summary>
    /// 保存派生查询的预算决策、耗时和连续采样峰值。
    /// </summary>
    private sealed record DominatorStressMeasurement(
        bool ExpectedToBuild,
        bool Built,
        double ElapsedMilliseconds,
        long BudgetBytes,
        long PrivateMemoryBeforeBytes,
        long PrivateMemoryPeakBytes,
        long WorkingSetBeforeBytes,
        long WorkingSetPeakBytes,
        long ManagedHeapBeforeBytes,
        long ManagedHeapPeakBytes);

    /// <summary>
    /// 表示一个时刻或一个阶段峰值的实际进程内存计数器。
    /// </summary>
    private readonly record struct ProcessMemorySample(
        long PrivateMemoryBytes,
        long WorkingSetBytes,
        long ManagedHeapBytes);
}
