using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Reflection;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Tests.Diagnostics;

/// <summary>
/// 验证受管端为原生保留 Profiler 创建的共享内存会话边界。
/// </summary>
[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not have incorrect suffix", Justification = "测试名说明边界行为。")]
public sealed class RetentionProfilerCaptureSessionTests
{
    private const long SegmentStateOffset = 88;
    private const long PublicationSequenceOffset = 96;
    private const long AcknowledgedSequenceOffset = 104;
    private const int PublishedSegmentState = 5;
    private const int ReusableSegmentState = 0;
    private const int ClosedWriterRegistration = int.MinValue;

    /// <summary>
    /// 原生附加必须由独立受管会话拥有映射和事件生命周期，不能泄漏到调用方。
    /// </summary>
    [TestMethod]
    public void RetentionProfilerCaptureSession_ExistsAsAnIndependentInteropBoundary()
    {
        var type = typeof(MemorySnapshotStore).Assembly.GetType(
            "DotnetAnalysis.Diagnostics.Windows.RetentionProfilerCaptureSession");

        Assert.IsNotNull(type);
    }

    /// <summary>
    /// 托管共享结构必须和 Windows x64 原生协议保持固定大小，否则 Profiler 会在错误偏移读写。
    /// </summary>
    [TestMethod]
    public void SharedMemoryRecords_MatchTheNativeX64ProtocolLayout()
    {
        Assert.AreEqual(112, Marshal.SizeOf<RetentionProfilerSharedHeader>());
        Assert.AreEqual(24, Marshal.SizeOf<RetentionProfilerRawObject>());
        Assert.AreEqual(16, Marshal.SizeOf<RetentionProfilerRawEdge>());
        Assert.AreEqual(32, Marshal.SizeOf<RetentionProfilerRawRoot>());
        Assert.AreEqual(1_552, Marshal.SizeOf<RetentionProfilerTypeEvidenceRecord>());
        Assert.AreEqual(3652, Marshal.SizeOf<RetentionProfilerAttachData>());
    }

    /// <summary>
    /// v6 必须使用多个预分配共享段而非单个 256 MiB 映射；段配置同时属于附加 ABI 的一部分。
    /// </summary>
    [TestMethod]
    public void RetentionProfilerCaptureSession_UsesVersion6PreallocatedSegments()
    {
        using var capture = new RetentionProfilerCaptureSession();
        var attach = capture.CreateAttachData();

        Assert.AreEqual(6U, attach.Version);
        Assert.AreEqual(4U, attach.SegmentCount);
        Assert.HasCount(4, capture.SegmentNames);
        Assert.IsGreaterThanOrEqualTo(64 * 1024U, capture.SegmentCapacityBytes);
    }

    /// <summary>
    /// v6 完成后必须将对象、边和根直接转存到临时 spool，而不是仅暴露会迫使调用方保留全量 List 的完成结果。
    /// </summary>
    [TestMethod]
    public void RetentionProfilerCaptureSession_ExposesDiskSpoolCompletionBoundary()
    {
        var method = typeof(RetentionProfilerCaptureSession).GetMethod("WaitForCompletionToSpoolAsync");

        Assert.IsNotNull(method);
        Assert.AreEqual(typeof(Task<RetentionProfilerRawCaptureSpool>), method.ReturnType);
    }

    /// <summary>
    /// 图回调结束而对象大小仍在冻结阶段回填时，Controller 必须先把已稳定的对象身份和引用边持续转存到 spool；
    /// 完成事件到达后再补齐对象大小与根证据，不能重新退化成只在完成后一次性复制整图。
    /// </summary>
    [TestMethod]
    [Timeout(15_000, CooperativeCancellation = true)]
    public async Task WaitForCompletionToSpoolAsync_WhenGraphIsFinalizing_DrainsGraphBeforeCompletionAndPatchesSizeAfterCompletion()
    {
        const int finalizingStatus = 2;
        var workingDirectory = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.FinalizingSpool.{Guid.NewGuid():N}");
        try
        {
            using var capture = new RetentionProfilerCaptureSession(64 * 1024, 2);
            using var targetExited = new ManualResetEvent(false);
            WriteGraphRecords(capture, finalizingStatus, objectSizeBytes: 0);

            var spoolTask = capture.WaitForCompletionToSpoolAsync(
                workingDirectory,
                targetExited,
                CancellationToken.None);
            var drainedBeforeCompletion = await WaitForGraphSpoolAsync(workingDirectory, CancellationToken.None);

            WriteGraphRecords(capture, (int)RetentionProfilerCaptureStatus.Completed, objectSizeBytes: 64);
            using (var completedEvent = EventWaitHandle.OpenExisting(capture.CompletionEventName))
            {
                completedEvent.Set();
            }

            using var spool = await spoolTask;
            var objectBytes = await File.ReadAllBytesAsync(spool.ObjectPath, CancellationToken.None);

            Assert.IsTrue(drainedBeforeCompletion, "Finalizing 阶段必须在完成事件前已向 spool 转存对象和边记录。");
            Assert.AreEqual(1L, spool.ObjectCount);
            Assert.AreEqual(1L, spool.EdgeCount);
            Assert.AreEqual(1L, spool.RootCount);
            Assert.AreEqual(0x101UL, BinaryPrimitives.ReadUInt64LittleEndian(objectBytes));
            Assert.AreEqual(0x202UL, BinaryPrimitives.ReadUInt64LittleEndian(objectBytes.AsSpan(sizeof(ulong))));
            Assert.AreEqual(64UL, BinaryPrimitives.ReadUInt64LittleEndian(objectBytes.AsSpan(sizeof(ulong) * 2)));
        }
        finally
        {
            if (Directory.Exists(workingDirectory))
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Controller 必须在 Capturing 期间按发布序列持续转存并确认共享段，使两段能够承载超过一次性总容量的记录；
    /// 完成时的最后一个部分段只转存一次，且复用不能改变对象顺序或丢失记录。
    /// </summary>
    [TestMethod]
    [Timeout(15_000, CooperativeCancellation = true)]
    public async Task WaitForCompletionToSpoolAsync_DuringCapturing_DrainsAcknowledgesAndReusesSegmentsInSequence()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.CapturingRing.{Guid.NewGuid():N}");
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var targetExited = new ManualResetEvent(false);
        var capture = new RetentionProfilerCaptureSession(64 * 1024, 2);
        Task<RetentionProfilerRawCaptureSpool>? spoolTask = null;
        try
        {
            SetCaptureStatus(capture, RetentionProfilerCaptureStatus.Capturing);
            spoolTask = capture.WaitForCompletionToSpoolAsync(
                workingDirectory,
                targetExited,
                cancellationSource.Token);

            PublishEdgeBatch(capture, segmentIndex: 1, publicationSequence: 1, [1, 2]);
            Assert.IsTrue(await WaitForAcknowledgementAsync(capture, 1, 1, cancellationSource.Token), "序列 1 必须在 Capturing 期间转存并确认。");
            PublishEdgeBatch(capture, segmentIndex: 0, publicationSequence: 2, [3, 4]);
            Assert.IsTrue(await WaitForAcknowledgementAsync(capture, 0, 2, cancellationSource.Token), "序列 2 必须在 Capturing 期间转存并确认。");
            PublishEdgeBatch(capture, segmentIndex: 1, publicationSequence: 3, [5, 6]);
            Assert.IsTrue(await WaitForAcknowledgementAsync(capture, 1, 3, cancellationSource.Token), "已确认的段必须能够以新序列复用。");

            PublishEdgeBatch(capture, segmentIndex: 0, publicationSequence: 4, [7]);
            SetCaptureStatus(capture, RetentionProfilerCaptureStatus.Completed);
            using (var completedEvent = EventWaitHandle.OpenExisting(capture.CompletionEventName))
            {
                completedEvent.Set();
            }

            using var spool = await spoolTask;
            spoolTask = null;
            var edgeBytes = await File.ReadAllBytesAsync(spool.EdgePath, cancellationSource.Token);
            var edgeTargets = Enumerable.Range(0, checked((int)spool.EdgeCount))
                .Select(index => BinaryPrimitives.ReadUInt64LittleEndian(
                    edgeBytes.AsSpan(index * RetentionProfilerRawCaptureSpool.EdgeRecordBytes + sizeof(ulong), sizeof(ulong))))
                .ToArray();

            Assert.AreEqual(7L, spool.EdgeCount, "两段一次性总容量为 4，环形复用后必须无损转存 7 条记录。");
            CollectionAssert.AreEqual(new ulong[] { 1, 2, 3, 4, 5, 6, 7 }, edgeTargets, "转存顺序必须由单调发布序列决定，不能按段索引重排。");
        }
        finally
        {
            cancellationSource.Cancel();
            if (spoolTask is not null)
            {
                try
                {
                    await spoolTask;
                }
                catch (OperationCanceledException)
                {
                }
            }
            capture.Dispose();
            if (Directory.Exists(workingDirectory))
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// 取消等待 CLR 分离只应取消调用方的等待，不能被 15 秒的原生超时阻塞。
    /// </summary>
    [TestMethod]
    public async Task WaitForDetachAsync_WhenCancellationOccursAfterWaitingBegins_StopsPromptly()
    {
        using var capture = new RetentionProfilerCaptureSession();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var waitTask = capture.WaitForDetachAsync(cancellationSource.Token);
        var completedTask = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.AreSame(waitTask, completedTask, "取消必须中断分离等待，不能等待原生 15 秒超时。");
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await waitTask);
    }

    /// <summary>
    /// 原生记录区容量耗尽必须映射为稳定错误码，不能被包装成普通捕获失败或伪造成功快照。
    /// </summary>
    [TestMethod]
    public void NativeCaptureFailure_WhenBufferIsExhausted_UsesDedicatedErrorCode()
    {
        var factory = typeof(RetentionProfilerCaptureSession).GetMethod(
            "CreateNativeCaptureFailure",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(factory);
        var header = new RetentionProfilerSharedHeader
        {
            FailureHResult = unchecked((int)0x8007000E)
        };

        var exception = (DiagnosticsException)factory.Invoke(null, [header])!;

        Assert.AreEqual(DiagnosticsErrorCode.ProfilerCaptureBufferExhausted, exception.ErrorCode);
    }

    /// <summary>
    /// 向两个预分配段写入一条完整图记录，并使用调用方指定的协议状态发布稳定读取边界。
    /// </summary>
    /// <param name="capture">拥有命名共享段的捕获会话。</param>
    /// <param name="status">要写入每个共享头部的协议状态。</param>
    /// <param name="objectSizeBytes">第一段对象记录中发布的浅表大小。</param>
    private static void WriteGraphRecords(RetentionProfilerCaptureSession capture, int status, nuint objectSizeBytes)
    {
        for (var index = 0; index < capture.SegmentNames.Count; index++)
        {
            using var mapping = MemoryMappedFile.OpenExisting(capture.SegmentNames[index], MemoryMappedFileRights.ReadWrite);
            using var view = mapping.CreateViewAccessor(0, capture.SegmentCapacityBytes, MemoryMappedFileAccess.ReadWrite);
            view.Read(0, out RetentionProfilerSharedHeader header);
            header.Status = status;
            header.LastProgressTickCount = Environment.TickCount64;
            if ((RetentionProfilerCaptureStatus)status is RetentionProfilerCaptureStatus.Finalizing)
            {
                header.PublicationSequence = index + 1;
                header.AcknowledgedSequence = 0;
                header.ActiveWriterCount = ClosedWriterRegistration;
                header.SegmentState = (int)RetentionProfilerSegmentState.Published;
            }
            if (index == 0)
            {
                header.ObjectCount = 1;
                header.EdgeCount = 1;
                header.RootCount = 1;
                var @object = new RetentionProfilerRawObject
                {
                    ObjectId = 0x101,
                    ClassId = 0x202,
                    SizeBytes = objectSizeBytes
                };
                var edge = new RetentionProfilerRawEdge
                {
                    SourceObjectId = 0x101,
                    TargetObjectId = 0x303
                };
                var root = new RetentionProfilerRawRoot
                {
                    ObjectId = 0x101,
                    RootKind = 1,
                    RootFlags = 0,
                    RootId = 0,
                    FunctionEvidenceIndex = uint.MaxValue,
                    Reserved = 0
                };
                view.Write(header.ObjectOffset, ref @object);
                view.Write(header.EdgeOffset, ref edge);
                view.Write(header.RootOffset, ref root);
            }

            view.Write(0, ref header);
        }
    }

    /// <summary>
    /// Profiler 附加后目标进程退出必须与完成和失败事件共同参与等待，并立即映射为稳定的目标退出错误，
    /// 不能继续等待 60 秒无进度看门狗。
    /// </summary>
    [TestMethod]
    [Timeout(5_000, CooperativeCancellation = true)]
    public async Task WaitForCompletionToSpoolAsync_WhenTargetExitsAfterAttach_FailsPromptlyAsTargetExited()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.TargetExit.{Guid.NewGuid():N}");
        using var targetExited = new ManualResetEvent(false);
        using var capture = new RetentionProfilerCaptureSession(64 * 1024, 2);
        try
        {
            SetCaptureStatus(capture, RetentionProfilerCaptureStatus.Capturing);
            var waitTask = capture.WaitForCompletionToSpoolAsync(
                workingDirectory,
                targetExited,
                CancellationToken.None);
            var stopwatch = Stopwatch.StartNew();

            targetExited.Set();
            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
                async () => await waitTask.WaitAsync(TimeSpan.FromSeconds(1)));

            Assert.AreEqual(DiagnosticsErrorCode.TargetExited, exception.ErrorCode);
            Assert.IsLessThan(TimeSpan.FromSeconds(1), stopwatch.Elapsed);
        }
        finally
        {
            if (Directory.Exists(workingDirectory))
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// 持续 drain 不得让 edge raw 文件超过保留快照格式的边记录上限，即使三个文件的合计空间仍然足够。
    /// </summary>
    [TestMethod]
    [Timeout(5_000, CooperativeCancellation = true)]
    public async Task WaitForCompletionToSpoolAsync_WhenRawEdgeLimitWouldBeExceeded_RejectsBeforeAppending()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), $"DotnetAnalysis.RawLimit.{Guid.NewGuid():N}");
        using var targetExited = new ManualResetEvent(false);
        using var capture = new RetentionProfilerCaptureSession(
            64 * 1024,
            2,
            maximumRawSpoolBytes: RetentionProfilerRawCaptureSpool.EdgeRecordBytes,
            maximumEdgeSpoolBytes: 0);
        RetentionProfilerRawCaptureSpool? unexpectedSpool = null;
        try
        {
            SetCaptureStatus(capture, RetentionProfilerCaptureStatus.Capturing);
            var waitTask = capture.WaitForCompletionToSpoolAsync(
                workingDirectory,
                targetExited,
                CancellationToken.None);
            _ = await WaitForRawSpoolDirectoryAsync(workingDirectory, CancellationToken.None);

            PublishEdgeBatch(capture, segmentIndex: 0, publicationSequence: 1, [1]);
            SetCaptureStatus(capture, RetentionProfilerCaptureStatus.Completed);
            using (var completedEvent = EventWaitHandle.OpenExisting(capture.CompletionEventName))
            {
                completedEvent.Set();
            }

            DiagnosticsException? exception = null;
            try
            {
                unexpectedSpool = await waitTask;
            }
            catch (DiagnosticsException caught)
            {
                exception = caught;
            }

            Assert.IsNotNull(exception, "超过 edge 文件独立记录上限前必须终止捕获。");
            Assert.AreEqual(DiagnosticsErrorCode.SnapshotStorageLimitReached, exception.ErrorCode);
            var evidenceDirectory = Directory.GetDirectories(workingDirectory, ".retention-raw-evidence.*").Single();
            Assert.AreEqual(0L, new FileInfo(Path.Combine(evidenceDirectory, "edges.raw.bin")).Length);
        }
        finally
        {
            unexpectedSpool?.Dispose();
            if (Directory.Exists(workingDirectory))
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// 将所有共享段推进到指定的全局捕获状态，同时保留每段独立的发布生命周期。
    /// </summary>
    /// <param name="capture">拥有命名共享段的捕获会话。</param>
    /// <param name="status">要发布的捕获状态。</param>
    private static void SetCaptureStatus(RetentionProfilerCaptureSession capture, RetentionProfilerCaptureStatus status)
    {
        foreach (var segmentName in capture.SegmentNames)
        {
            using var mapping = MemoryMappedFile.OpenExisting(segmentName, MemoryMappedFileRights.ReadWrite);
            using var view = mapping.CreateViewAccessor(0, capture.SegmentCapacityBytes, MemoryMappedFileAccess.ReadWrite);
            view.Read(0, out RetentionProfilerSharedHeader header);
            header.Status = (int)status;
            header.LastProgressTickCount = Environment.TickCount64;
            view.Write(0, ref header);
        }
    }

    /// <summary>
    /// 模拟原生生产者先写完固定宽度引用边，再以单调序列发布一个完整或最终部分段。
    /// </summary>
    /// <param name="capture">拥有命名共享段的捕获会话。</param>
    /// <param name="segmentIndex">要发布的共享段索引。</param>
    /// <param name="publicationSequence">本次发布的全局单调序列。</param>
    /// <param name="targetObjectIds">按回调顺序写入的目标对象标识。</param>
    private static void PublishEdgeBatch(
        RetentionProfilerCaptureSession capture,
        int segmentIndex,
        long publicationSequence,
        IReadOnlyList<ulong> targetObjectIds)
    {
        using var mapping = MemoryMappedFile.OpenExisting(capture.SegmentNames[segmentIndex], MemoryMappedFileRights.ReadWrite);
        using var view = mapping.CreateViewAccessor(0, capture.SegmentCapacityBytes, MemoryMappedFileAccess.ReadWrite);
        view.Read(0, out RetentionProfilerSharedHeader header);
        header.ObjectCount = 0;
        header.EdgeCapacity = 2;
        header.EdgeCount = targetObjectIds.Count;
        header.RootCount = 0;
        header.FunctionCount = 0;
        header.TypeCount = 0;
        header.ActiveWriterCount = ClosedWriterRegistration;
        header.LastProgressTickCount = Environment.TickCount64;
        for (var index = 0; index < targetObjectIds.Count; index++)
        {
            var edge = new RetentionProfilerRawEdge(0x100, (nuint)targetObjectIds[index]);
            view.Write(checked((long)header.EdgeOffset + index * RetentionProfilerRawCaptureSpool.EdgeRecordBytes), ref edge);
        }

        view.Write(0, ref header);
        view.Write(PublicationSequenceOffset, publicationSequence);
        Thread.MemoryBarrier();
        view.Write(SegmentStateOffset, PublishedSegmentState);
    }

    /// <summary>
    /// 有界等待 Controller 对指定发布序列完成落盘确认并把段归还为可复用状态。
    /// </summary>
    /// <param name="capture">拥有命名共享段的捕获会话。</param>
    /// <param name="segmentIndex">等待确认的共享段索引。</param>
    /// <param name="publicationSequence">期望确认的发布序列。</param>
    /// <param name="cancellationToken">取消当前测试等待的令牌。</param>
    /// <returns>在一秒内同时观测到匹配确认序列和可复用状态时返回 <see langword="true"/>。</returns>
    private static async Task<bool> WaitForAcknowledgementAsync(
        RetentionProfilerCaptureSession capture,
        int segmentIndex,
        long publicationSequence,
        CancellationToken cancellationToken)
    {
        using var mapping = MemoryMappedFile.OpenExisting(capture.SegmentNames[segmentIndex], MemoryMappedFileRights.ReadWrite);
        using var view = mapping.CreateViewAccessor(0, capture.SegmentCapacityBytes, MemoryMappedFileAccess.ReadWrite);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var acknowledgedSequence = view.ReadInt64(AcknowledgedSequenceOffset);
            Thread.MemoryBarrier();
            if (acknowledgedSequence == publicationSequence && view.ReadInt32(SegmentStateOffset) == ReusableSegmentState)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }

        return false;
    }

    /// <summary>
    /// 在有界等待中观测 Controller 是否已将 Finalizing 阶段的对象和边写入临时 spool。
    /// </summary>
    /// <param name="workingDirectory">捕获会话创建原始 spool 的父目录。</param>
    /// <param name="cancellationToken">取消轮询等待的令牌。</param>
    /// <returns>若在完成事件前看到非空对象和边文件则返回 <see langword="true"/>。</returns>
    private static async Task<bool> WaitForGraphSpoolAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = Directory.Exists(workingDirectory)
                ? Directory.EnumerateFiles(workingDirectory, "*.raw.bin", SearchOption.AllDirectories).ToArray()
                : [];
            if (files.Any(path => Path.GetFileName(path) == "objects.raw.bin" && new FileInfo(path).Length > 0)
                && files.Any(path => Path.GetFileName(path) == "edges.raw.bin" && new FileInfo(path).Length > 0))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        return false;
    }

    /// <summary>
    /// 等待捕获会话完成三个空 raw 文件的初始化，并返回本次捕获独占的 spool 目录。
    /// </summary>
    /// <param name="workingDirectory">捕获会话创建 raw spool 的父目录。</param>
    /// <param name="cancellationToken">取消测试等待的令牌。</param>
    /// <returns>包含三个固定宽度 raw 文件的目录。</returns>
    private static async Task<string> WaitForRawSpoolDirectoryAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Directory.Exists(workingDirectory)
                ? Directory.EnumerateDirectories(workingDirectory, ".retention-raw.*").SingleOrDefault()
                : null;
            if (directory is not null
                && File.Exists(Path.Combine(directory, "objects.raw.bin"))
                && File.Exists(Path.Combine(directory, "edges.raw.bin"))
                && File.Exists(Path.Combine(directory, "roots.raw.bin")))
            {
                return directory;
            }

            await Task.Delay(10, cancellationToken);
        }

        throw new TimeoutException("捕获会话未在一秒内初始化 raw spool。");
    }
}
