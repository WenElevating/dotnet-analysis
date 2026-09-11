using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using DotnetAnalysis.Application.Contracts.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 创建并拥有一次原生保留 Profiler 附加所需的命名共享内存和完成事件。
/// </summary>
/// <remarks>
/// 该类型只管理诊断进程内的 IPC 资源。目标进程的 Profiler 只能写入预先划分的记录区，
/// 本类型在 Capturing 阶段持续转存已发布图记录，并在完成事件建立的 happens-before 边界后补齐对象大小和根证据。
/// </remarks>
internal sealed class RetentionProfilerCaptureSession : IDisposable
{
    private const uint ProtocolVersion = 6;
    private const uint SharedMemoryMagic = 0x50415244;
    private const int MinimumMappingBytes = 64 * 1024;
    private const int DefaultSegmentCapacityBytes = 64 * 1024 * 1024;
    private const int DefaultSegmentCount = 4;
    private const int ObjectRecordBytes = 24;
    private const int EdgeRecordBytes = 16;
    private const int RootRecordBytes = 32;
    private const int FunctionRecordBytes = 1_552;
    private const int TypeRecordBytes = 1_552;
    private const int HeaderBytes = 112;
    private const int SegmentStateOffset = 88;
    private const int PublicationSequenceOffset = 96;
    private const int AcknowledgedSequenceOffset = 104;
    private const int ClosedWriterRegistration = int.MinValue;
    private const int MaximumFunctionEvidence = 1_024;
    private const int MaximumTypeEvidence = 4_096;
    private readonly MemoryMappedFile[] _mappings;
    private readonly MemoryMappedViewAccessor[] _views;
    private readonly EventWaitHandle _completionEvent;
    private readonly EventWaitHandle _failureEvent;
    private readonly EventWaitHandle _detachEvent;
    private readonly long _maximumRawSpoolBytes;
    private readonly long _maximumObjectSpoolBytes;
    private readonly long _maximumEdgeSpoolBytes;
    private readonly long _maximumRootSpoolBytes;
    private bool _disposed;

    /// <summary>
    /// 创建一个已初始化且仅供本次附加使用的会话。
    /// </summary>
    /// <param name="segmentCapacityBytes">每个预分配共享段容量；默认四段各 64 MiB。</param>
    /// <param name="segmentCount">共享段数量，必须为 2 至 4。</param>
    /// <param name="maximumRawSpoolBytes">三个 raw 文件允许并存的最大总字节数；生产默认采用格式记录上限。</param>
    /// <param name="maximumObjectSpoolBytes">对象 raw 文件允许的最大字节数；生产默认采用格式对象记录上限。</param>
    /// <param name="maximumEdgeSpoolBytes">引用边 raw 文件允许的最大字节数；生产默认采用格式边记录上限。</param>
    /// <param name="maximumRootSpoolBytes">GC 根 raw 文件允许的最大字节数；生产默认采用格式根记录上限。</param>
    public RetentionProfilerCaptureSession(
        int segmentCapacityBytes = DefaultSegmentCapacityBytes,
        int segmentCount = DefaultSegmentCount,
        long maximumRawSpoolBytes = RetentionProfilerRawCaptureSpool.MaximumSupportedBytes,
        long maximumObjectSpoolBytes = RetentionProfilerRawCaptureSpool.MaximumObjectBytes,
        long maximumEdgeSpoolBytes = RetentionProfilerRawCaptureSpool.MaximumEdgeBytes,
        long maximumRootSpoolBytes = RetentionProfilerRawCaptureSpool.MaximumRootBytes)
    {
        if (segmentCapacityBytes < MinimumMappingBytes || segmentCapacityBytes > int.MaxValue - HeaderBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentCapacityBytes));
        }
        if (segmentCount is < 2 or > DefaultSegmentCount)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentCount));
        }
        if (maximumRawSpoolBytes < 0 || maximumRawSpoolBytes > RetentionProfilerRawCaptureSpool.MaximumSupportedBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRawSpoolBytes));
        }
        if (maximumObjectSpoolBytes < 0 || maximumObjectSpoolBytes > RetentionProfilerRawCaptureSpool.MaximumObjectBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumObjectSpoolBytes));
        }
        if (maximumEdgeSpoolBytes < 0 || maximumEdgeSpoolBytes > RetentionProfilerRawCaptureSpool.MaximumEdgeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEdgeSpoolBytes));
        }
        if (maximumRootSpoolBytes < 0 || maximumRootSpoolBytes > RetentionProfilerRawCaptureSpool.MaximumRootBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRootSpoolBytes));
        }

        _maximumRawSpoolBytes = maximumRawSpoolBytes;
        _maximumObjectSpoolBytes = maximumObjectSpoolBytes;
        _maximumEdgeSpoolBytes = maximumEdgeSpoolBytes;
        _maximumRootSpoolBytes = maximumRootSpoolBytes;
        var token = Guid.NewGuid().ToString("N");
        SegmentNames = Enumerable.Range(0, segmentCount)
            .Select(index => $"DotnetAnalysis.Retention.{token}.Segment{index}")
            .ToArray();
        CompletionEventName = $"DotnetAnalysis.Retention.{token}.Complete";
        FailureEventName = $"DotnetAnalysis.Retention.{token}.Failed";
        DetachEventName = $"DotnetAnalysis.Retention.{token}.Detached";
        SegmentCapacityBytes = checked((uint)segmentCapacityBytes);
        MappingCapacityBytes = checked((uint)(segmentCapacityBytes * segmentCount));
        var mappings = new MemoryMappedFile?[segmentCount];
        var views = new MemoryMappedViewAccessor?[segmentCount];
        EventWaitHandle? completionEvent = null;
        EventWaitHandle? failureEvent = null;
        EventWaitHandle? detachEvent = null;
        try
        {
            completionEvent = new EventWaitHandle(false, EventResetMode.ManualReset, CompletionEventName);
            failureEvent = new EventWaitHandle(false, EventResetMode.ManualReset, FailureEventName);
            detachEvent = new EventWaitHandle(false, EventResetMode.ManualReset, DetachEventName);

            var layout = CreateLayout(SegmentCapacityBytes);
            for (var index = 0; index < segmentCount; index++)
            {
                mappings[index] = MemoryMappedFile.CreateNew(SegmentNames[index], segmentCapacityBytes, MemoryMappedFileAccess.ReadWrite);
                views[index] = mappings[index]!.CreateViewAccessor(0, segmentCapacityBytes, MemoryMappedFileAccess.ReadWrite);
                var header = new RetentionProfilerSharedHeader
                {
                    Magic = SharedMemoryMagic,
                    Version = ProtocolVersion,
                    CapacityBytes = SegmentCapacityBytes,
                    Status = (int)RetentionProfilerCaptureStatus.Pending,
                    ObjectOffset = layout.ObjectOffset,
                    ObjectCapacity = layout.ObjectCapacity,
                    EdgeOffset = layout.EdgeOffset,
                    EdgeCapacity = layout.EdgeCapacity,
                    RootOffset = layout.RootOffset,
                    RootCapacity = layout.RootCapacity,
                    FunctionOffset = layout.FunctionOffset,
                    FunctionCapacity = layout.FunctionCapacity,
                    FunctionCount = 0,
                    TypeOffset = layout.TypeOffset,
                    TypeCapacity = layout.TypeCapacity,
                    TypeCount = 0,
                    FailureHResult = 0,
                    LastProgressTickCount = Environment.TickCount64,
                    SegmentState = (int)RetentionProfilerSegmentState.Reusable,
                    ActiveWriterCount = 0,
                    PublicationSequence = 0,
                    AcknowledgedSequence = 0
                };
                views[index]!.Write(0, ref header);
            }

            _mappings = mappings!;
            _views = views!;
            _completionEvent = completionEvent;
            _failureEvent = failureEvent;
            _detachEvent = detachEvent;
        }
        catch
        {
            foreach (var view in views)
            {
                view?.Dispose();
            }

            foreach (var mapping in mappings)
            {
                mapping?.Dispose();
            }

            detachEvent?.Dispose();
            failureEvent?.Dispose();
            completionEvent?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 原生 Profiler 要打开的预分配共享段名称；顺序即 native 溢出时切换段的稳定顺序。
    /// </summary>
    public IReadOnlyList<string> SegmentNames { get; }

    /// <summary>
    /// 每个共享段容量。
    /// </summary>
    public uint SegmentCapacityBytes { get; }

    /// <summary>
    /// 原生 Profiler 在成功冻结数据后设置的命名事件。
    /// </summary>
    public string CompletionEventName { get; }

    /// <summary>
    /// 原生 Profiler 在协议、容量或 CLR 失败时设置的命名事件。
    /// </summary>
    public string FailureEventName { get; }

    /// <summary>
    /// 原生 Profiler 已完成 CLR 分离时设置的命名事件。
    /// </summary>
    public string DetachEventName { get; }

    /// <summary>
    /// 所有共享段的总预分配容量。
    /// </summary>
    public uint MappingCapacityBytes { get; }

    /// <summary>
    /// 创建传给 Controller 和 CLR InitializeForAttach 的固定布局参数。
    /// </summary>
    public RetentionProfilerAttachData CreateAttachData()
    {
        ThrowIfDisposed();
        return new RetentionProfilerAttachData
        {
            Version = ProtocolVersion,
            SegmentCapacityBytes = SegmentCapacityBytes,
            SegmentCount = checked((uint)SegmentNames.Count),
            SegmentName0 = SegmentNames[0],
            SegmentName1 = SegmentNames[1],
            SegmentName2 = SegmentNames.Count > 2 ? SegmentNames[2] : string.Empty,
            SegmentName3 = SegmentNames.Count > 3 ? SegmentNames[3] : string.Empty,
            CompletionEventName = CompletionEventName,
            FailureEventName = FailureEventName,
            DetachEventName = DetachEventName
        };
    }

    /// <summary>
    /// 等待 CLR 确认当前附加 Profiler 已分离，确保后续捕获不会与上一轮附加重叠。
    /// </summary>
    /// <param name="cancellationToken">取消当前等待但不强制中断 CLR 分离的令牌。</param>
    /// <returns>CLR 在 15 秒内确认分离时完成的任务。</returns>
    /// <exception cref="OperationCanceledException">调用方取消等待时引发；CLR 分离仍会在目标进程内继续。</exception>
    /// <exception cref="DiagnosticsException">CLR 未在限定时间内确认分离时引发。</exception>
    public async Task WaitForDetachAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var waitResult = await Task.Run(
            () => WaitHandle.WaitAny([_detachEvent, cancellationToken.WaitHandle], TimeSpan.FromSeconds(15)))
            .ConfigureAwait(false);
        if (waitResult == 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (waitResult != 0)
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.ProfilerCaptureFailed, "原生保留 Profiler 未在 15 秒内完成 CLR 分离。");
        }
    }

    /// <summary>
    /// 等待 Profiler 通过完成或失败事件结束；60 秒没有任何原生进度即稳定失败。
    /// </summary>
    /// <param name="cancellationToken">取消当前等待但不取消已附加 Profiler 的令牌。</param>
    /// <returns>完成时的原始共享内存记录。</returns>
    /// <exception cref="DiagnosticsException">失败事件、无进度或无效状态时引发。</exception>
    public async Task<RetentionProfilerRawCapture> WaitForCompletionAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        const int pollMilliseconds = 1_000;
        const long noProgressLimitMilliseconds = 60_000;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signal = await Task.Run(
                () => WaitHandle.WaitAny([_completionEvent, _failureEvent], pollMilliseconds),
                cancellationToken).ConfigureAwait(false);
            if (signal == 0)
            {
                return ReadCompletedCapture();
            }
            if (signal == 1)
            {
                throw CreateNativeCaptureFailure(ReadHeader());
            }

            var header = ReadHeader();
            if ((RetentionProfilerCaptureStatus)header.Status is RetentionProfilerCaptureStatus.Failed)
            {
                throw CreateNativeCaptureFailure(header);
            }
            if (Environment.TickCount64 - header.LastProgressTickCount > noProgressLimitMilliseconds)
            {
                throw new DiagnosticsException(DiagnosticsErrorCode.ProfilerCaptureFailed, "原生保留 Profiler 在 60 秒内没有采集进度。");
            }
        }
    }

    /// <summary>
    /// 读取冻结后的原始记录；调用方必须在成功完成事件后调用。
    /// </summary>
    private RetentionProfilerRawCapture ReadCompletedCapture()
    {
        var objects = new List<RetentionProfilerRawObject>();
        var edges = new List<RetentionProfilerRawEdge>();
        var roots = new List<RetentionProfilerRawRoot>();
        var functions = new List<RetentionProfilerRawFunction>();
        var types = new List<RetentionProfilerRawType>();
        for (var segmentIndex = 0; segmentIndex < _views.Length; segmentIndex++)
        {
            _views[segmentIndex].Read(0, out RetentionProfilerSharedHeader header);
            ValidateCompletedHeader(header, segmentIndex == 0);
            var segmentFunctions = ReadFunctionRecords(_views[segmentIndex], header);
            var functionOffset = functions.Count;
            functions.AddRange(segmentFunctions);
            types.AddRange(ReadTypeRecords(_views[segmentIndex], header));
            var segmentObjects = new RetentionProfilerRawObject[header.ObjectCount];
            var segmentEdges = new RetentionProfilerRawEdge[header.EdgeCount];
            var segmentRoots = new RetentionProfilerRawRoot[header.RootCount];
            ReadRecords(_views[segmentIndex], header.ObjectOffset, segmentObjects);
            ReadRecords(_views[segmentIndex], header.EdgeOffset, segmentEdges);
            ReadRecords(_views[segmentIndex], header.RootOffset, segmentRoots);
            foreach (ref var root in segmentRoots.AsSpan())
            {
                if (root.FunctionEvidenceIndex != uint.MaxValue)
                {
                    root.FunctionEvidenceIndex = checked(root.FunctionEvidenceIndex + (uint)functionOffset);
                }
            }
            objects.AddRange(segmentObjects);
            edges.AddRange(segmentEdges);
            roots.AddRange(segmentRoots);
        }

        return new RetentionProfilerRawCapture(objects, edges, roots, functions, types);
    }

    /// <summary>
    /// 从映射内固定位置读取非托管记录数组。
    /// </summary>
    private static void ReadRecords<T>(MemoryMappedViewAccessor view, uint offset, T[] records)
        where T : struct
    {
        if (records.Length != 0)
        {
            view.ReadArray(offset, records, 0, records.Length);
        }
    }

    /// <summary>
    /// 读取固定 UTF-16 函数证据区；只有 CLR 成功解析的栈根才会有非空名称。
    /// </summary>
    private static RetentionProfilerRawFunction[] ReadFunctionRecords(MemoryMappedViewAccessor view, RetentionProfilerSharedHeader header)
    {
        var functions = new RetentionProfilerRawFunction[header.FunctionCount];
        if (functions.Length == 0)
        {
            return functions;
        }

        var bytes = new byte[checked(functions.Length * FunctionRecordBytes)];
        view.ReadArray(header.FunctionOffset, bytes, 0, bytes.Length);
        for (var index = 0; index < functions.Length; index++)
        {
            var offset = index * FunctionRecordBytes;
            var functionId = IntPtr.Size == sizeof(long)
                ? BitConverter.ToUInt64(bytes, offset)
                : BitConverter.ToUInt32(bytes, offset);
            functions[index] = new RetentionProfilerRawFunction(
                (nuint)functionId,
                ReadFixedUnicodeString(bytes.AsSpan(offset + sizeof(ulong), 1_024)),
                ReadFixedUnicodeString(bytes.AsSpan(offset + sizeof(ulong) + 1_024, 520)));
        }

        return functions;
    }

    /// <summary>
    /// 读取固定 UTF-16 类型证据区；无法由 CLR Metadata API 验证的 ClassID 不会出现在结果中。
    /// </summary>
    private static RetentionProfilerRawType[] ReadTypeRecords(MemoryMappedViewAccessor view, RetentionProfilerSharedHeader header)
    {
        var types = new RetentionProfilerRawType[header.TypeCount];
        if (types.Length == 0)
        {
            return types;
        }

        var bytes = new byte[checked(types.Length * TypeRecordBytes)];
        view.ReadArray(header.TypeOffset, bytes, 0, bytes.Length);
        for (var index = 0; index < types.Length; index++)
        {
            var offset = index * TypeRecordBytes;
            var classId = IntPtr.Size == sizeof(long)
                ? BitConverter.ToUInt64(bytes, offset)
                : BitConverter.ToUInt32(bytes, offset);
            types[index] = new RetentionProfilerRawType(
                (nuint)classId,
                ReadFixedUnicodeString(bytes.AsSpan(offset + sizeof(ulong), 1_024)),
                ReadFixedUnicodeString(bytes.AsSpan(offset + sizeof(ulong) + 1_024, 520)));
        }

        return types;
    }

    /// <summary>
    /// 将固定长度、空字符终止的 UTF-16 缓冲区转为托管字符串。
    /// </summary>
    private static string? ReadFixedUnicodeString(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.Unicode.GetString(bytes);
        var terminator = text.IndexOf('\0');
        return terminator <= 0 ? null : text[..terminator];
    }

    /// <summary>
    /// 校验单个 v6 共享段的容量和记录计数；首段必须由完成事件对应的 native 状态冻结，后续段只要记录边界有效即可。
    /// </summary>
    private static void ValidateCompletedHeader(RetentionProfilerSharedHeader header, bool requireCompletedStatus)
    {
        if ((requireCompletedStatus && (RetentionProfilerCaptureStatus)header.Status is not RetentionProfilerCaptureStatus.Completed)
            || header.Magic != SharedMemoryMagic
            || header.Version != ProtocolVersion
            || header.CapacityBytes == 0
            || header.ObjectCount < 0 || header.EdgeCount < 0 || header.RootCount < 0
            || header.FunctionCount < 0 || header.TypeCount < 0
            || (uint)header.ObjectCount > header.ObjectCapacity
            || (uint)header.EdgeCount > header.EdgeCapacity
            || (uint)header.RootCount > header.RootCapacity
            || (uint)header.FunctionCount > header.FunctionCapacity
            || (uint)header.TypeCount > header.TypeCapacity)
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.ProfilerCaptureFailed, "原生保留 Profiler 返回了不完整或越界的共享内存数据。");
        }
    }

    /// <summary>
    /// 在所有物理段中查找精确的下一发布序列；序列而非段索引决定 spool 的稳定记录顺序。
    /// </summary>
    private int FindPublishedSegment(long expectedSequence)
    {
        var result = -1;
        for (var segmentIndex = 0; segmentIndex < _views.Length; segmentIndex++)
        {
            if ((RetentionProfilerSegmentState)_views[segmentIndex].ReadInt32(SegmentStateOffset)
                    is not RetentionProfilerSegmentState.Published
                || _views[segmentIndex].ReadInt64(PublicationSequenceOffset) != expectedSequence)
            {
                continue;
            }
            if (result >= 0)
            {
                throw new DiagnosticsException(DiagnosticsErrorCode.ProfilerCaptureFailed, "原生保留 Profiler 返回了重复的共享段发布序列。");
            }
            result = segmentIndex;
        }
        return result;
    }

    /// <summary>
    /// 捕获完成后确认当前映射中不存在未消费的更高序列或仍在封存的段，否则拒绝不完整快照。
    /// </summary>
    private void ValidateNoMissingCompletedPublication(long nextExpectedSequence)
    {
        var maximumPublishedSequence = 0L;
        for (var segmentIndex = 0; segmentIndex < _views.Length; segmentIndex++)
        {
            var state = (RetentionProfilerSegmentState)_views[segmentIndex].ReadInt32(SegmentStateOffset);
            var sequence = _views[segmentIndex].ReadInt64(PublicationSequenceOffset);
            maximumPublishedSequence = Math.Max(maximumPublishedSequence, sequence);
            if (state is RetentionProfilerSegmentState.Initializing
                or RetentionProfilerSegmentState.Writing
                or RetentionProfilerSegmentState.AssigningSequence
                or RetentionProfilerSegmentState.Sealing)
            {
                throw new DiagnosticsException(DiagnosticsErrorCode.ProfilerCaptureFailed, "原生保留 Profiler 在完成事件后仍有未冻结的共享段。");
            }
            if (state is RetentionProfilerSegmentState.Published && sequence < nextExpectedSequence)
            {
                throw new DiagnosticsException(DiagnosticsErrorCode.ProfilerCaptureFailed, "原生保留 Profiler 重复发布了已经确认的共享段代际。");
            }
        }
        if (nextExpectedSequence <= maximumPublishedSequence)
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.ProfilerCaptureFailed, "原生保留 Profiler 的共享段发布序列不连续。");
        }
    }

    /// <summary>
    /// 验证 Published acquire 屏障后的计数、代际和只追加 ledger，拒绝半写、回退或越界数据。
    /// </summary>
    private static void ValidatePublishedHeader(
        RetentionProfilerSharedHeader header,
        RetentionProfilerSpoolDrainState drainState,
        int segmentIndex)
    {
        ValidateCompletedHeader(header, requireCompletedStatus: false);
        if ((RetentionProfilerSegmentState)header.SegmentState is not RetentionProfilerSegmentState.Published
            || header.ActiveWriterCount != ClosedWriterRegistration
            || header.PublicationSequence != drainState.NextPublicationSequence
            || header.AcknowledgedSequence >= header.PublicationSequence
            || header.ObjectCount < drainState.DrainedObjectCounts[segmentIndex]
            || header.RootCount < drainState.DrainedRootCounts[segmentIndex])
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.ProfilerCaptureFailed, "原生保留 Profiler 返回了不完整、陈旧或回退的共享段发布。");
        }
    }

    /// <summary>
    /// 在三个 spool 均完成异步刷新后，以 release 顺序写入精确确认序列并归还物理段。
    /// </summary>
    private static void AcknowledgePublishedSegment(MemoryMappedViewAccessor view, long publicationSequence)
    {
        if ((RetentionProfilerSegmentState)view.ReadInt32(SegmentStateOffset) is not RetentionProfilerSegmentState.Published
            || view.ReadInt64(PublicationSequenceOffset) != publicationSequence)
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.ProfilerCaptureFailed, "原生保留 Profiler 在转存期间改变了共享段发布代际。");
        }

        view.Write(AcknowledgedSequenceOffset, publicationSequence);
        Thread.MemoryBarrier();
        view.Write(SegmentStateOffset, (int)RetentionProfilerSegmentState.Reusable);
    }

    /// <summary>
    /// 等待 Profiler 完成后将对象、边和根按固定宽度直接转存到磁盘 spool；
    /// 函数和类型证据受共享协议容量限制，因此可以保留为小型托管元数据。
    /// </summary>
    /// <param name="workingDirectory">当前快照目录中允许创建一次性原始记录 spool 的路径。</param>
    /// <param name="targetExitWaitHandle">绑定到本次已附加目标进程实例的退出等待句柄。</param>
    /// <param name="cancellationToken">取消当前等待或转存操作的令牌。</param>
    /// <returns>拥有原始记录 spool 的结果；调用方完成快照转换后必须释放它。</returns>
    /// <exception cref="DiagnosticsException">目标退出、失败事件、无进度、无效状态或 spool 写入失败时引发。</exception>
    public async Task<RetentionProfilerRawCaptureSpool> WaitForCompletionToSpoolAsync(
        string workingDirectory,
        WaitHandle targetExitWaitHandle,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(targetExitWaitHandle);
        ThrowIfDisposed();
        const int pollMilliseconds = 100;
        const long noProgressLimitMilliseconds = 60_000;
        var spool = await RetentionProfilerRawCaptureSpool.CreateEmptyAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        var drainState = new RetentionProfilerSpoolDrainState(_views.Length);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var signal = await Task.Run(
                    () => WaitHandle.WaitAny(
                        [_completionEvent, _failureEvent, targetExitWaitHandle, cancellationToken.WaitHandle],
                        pollMilliseconds),
                    cancellationToken).ConfigureAwait(false);
                if (signal == 2)
                {
                    throw new DiagnosticsException(
                        DiagnosticsErrorCode.TargetExited,
                        "目标进程在保留分析 Profiler 捕获完成前退出。");
                }
                if (signal == 3)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var header = ReadHeader();
                var status = (RetentionProfilerCaptureStatus)header.Status;
                if (signal == 1 || status is RetentionProfilerCaptureStatus.Failed)
                {
                    throw CreateNativeCaptureFailure(header);
                }
                await DrainPublishedSegmentsToSpoolAsync(
                    spool,
                    drainState,
                    captureCompleted: signal == 0 || status is RetentionProfilerCaptureStatus.Completed,
                    cancellationToken).ConfigureAwait(false);
                if (signal == 0 || status is RetentionProfilerCaptureStatus.Completed)
                {
                    return await CompleteSpoolAsync(spool, drainState, cancellationToken).ConfigureAwait(false);
                }

                if (Environment.TickCount64 - header.LastProgressTickCount > noProgressLimitMilliseconds)
                {
                    throw new DiagnosticsException(DiagnosticsErrorCode.ProfilerCaptureFailed, "原生保留 Profiler 在 60 秒内没有采集进度。");
                }
            }
        }
        catch
        {
            spool.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 在 Capturing 期间按全局单调序列持续转存 Published 段，并在文件刷新后确认精确代际供生产者复用。
    /// </summary>
    private async Task DrainPublishedSegmentsToSpoolAsync(
        RetentionProfilerRawCaptureSpool spool,
        RetentionProfilerSpoolDrainState drainState,
        bool captureCompleted,
        CancellationToken cancellationToken)
    {
        await using var objectStream = new FileStream(spool.ObjectPath, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var edgeStream = new FileStream(spool.EdgePath, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var rootStream = new FileStream(spool.RootPath, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var publishedSegmentIndex = FindPublishedSegment(drainState.NextPublicationSequence);
            if (publishedSegmentIndex < 0)
            {
                if (captureCompleted)
                {
                    ValidateNoMissingCompletedPublication(drainState.NextPublicationSequence);
                }
                return;
            }

            var view = _views[publishedSegmentIndex];
            Thread.MemoryBarrier();
            view.Read(0, out RetentionProfilerSharedHeader header);
            ValidatePublishedHeader(header, drainState, publishedSegmentIndex);

            var objectStart = drainState.DrainedObjectCounts[publishedSegmentIndex];
            var objectCount = header.ObjectCount - objectStart;
            var objectDestinationOffset = objectStream.Position;
            var rootStart = drainState.DrainedRootCounts[publishedSegmentIndex];
            var rootCount = header.RootCount - rootStart;
            var rootDestinationOffset = rootStream.Position;
            var additionalObjectBytes = checked((long)objectCount * ObjectRecordBytes);
            var additionalEdgeBytes = checked((long)header.EdgeCount * EdgeRecordBytes);
            var additionalRootBytes = checked((long)rootCount * RootRecordBytes);
            EnsureRawSpoolCanAppend(
                objectStream,
                edgeStream,
                rootStream,
                additionalObjectBytes,
                additionalEdgeBytes,
                additionalRootBytes);
            await CopyFixedRecordsAsync(
                view,
                checked(header.ObjectOffset + (uint)objectStart * ObjectRecordBytes),
                checked((long)objectCount * ObjectRecordBytes),
                objectStream,
                cancellationToken).ConfigureAwait(false);
            await CopyFixedRecordsAsync(
                view,
                header.EdgeOffset,
                checked((long)header.EdgeCount * EdgeRecordBytes),
                edgeStream,
                cancellationToken).ConfigureAwait(false);
            await CopyFixedRecordsAsync(
                view,
                checked(header.RootOffset + (uint)rootStart * RootRecordBytes),
                checked((long)rootCount * RootRecordBytes),
                rootStream,
                cancellationToken).ConfigureAwait(false);

            await objectStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await edgeStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await rootStream.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (objectCount > 0)
            {
                drainState.ObjectRanges.Add(new RetentionProfilerPublishedRange(
                    publishedSegmentIndex,
                    objectStart,
                    objectCount,
                    objectDestinationOffset));
            }
            if (rootCount > 0)
            {
                drainState.RootRanges.Add(new RetentionProfilerPublishedRange(
                    publishedSegmentIndex,
                    rootStart,
                    rootCount,
                    rootDestinationOffset));
            }
            drainState.DrainedObjectCounts[publishedSegmentIndex] = header.ObjectCount;
            drainState.DrainedRootCounts[publishedSegmentIndex] = header.RootCount;
            AcknowledgePublishedSegment(view, header.PublicationSequence);
            drainState.NextPublicationSequence++;
        }
    }

    /// <summary>
    /// 在写入一个完整发布批次前验证三个 raw 文件不会超过启动时容量租约采用的格式上限。
    /// </summary>
    /// <param name="objectStream">当前对象 raw 文件流。</param>
    /// <param name="edgeStream">当前引用边 raw 文件流。</param>
    /// <param name="rootStream">当前 GC 根 raw 文件流。</param>
    /// <param name="additionalObjectBytes">本批次对象记录区将追加的字节数。</param>
    /// <param name="additionalEdgeBytes">本批次引用边记录区将追加的字节数。</param>
    /// <param name="additionalRootBytes">本批次 GC 根记录区将追加的字节数。</param>
    /// <exception cref="DiagnosticsException">追加会突破本次捕获允许的 raw spool 上限时引发。</exception>
    private void EnsureRawSpoolCanAppend(
        Stream objectStream,
        Stream edgeStream,
        Stream rootStream,
        long additionalObjectBytes,
        long additionalEdgeBytes,
        long additionalRootBytes)
    {
        var currentObjectBytes = objectStream.Length;
        var currentEdgeBytes = edgeStream.Length;
        var currentRootBytes = rootStream.Length;
        var currentBytes = checked(currentObjectBytes + currentEdgeBytes + currentRootBytes);
        var additionalBytes = checked(additionalObjectBytes + additionalEdgeBytes + additionalRootBytes);
        if (currentBytes > _maximumRawSpoolBytes
            || additionalBytes < 0
            || additionalBytes > _maximumRawSpoolBytes - currentBytes)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.SnapshotStorageLimitReached,
                "Profiler 原始记录超过保留快照格式和启动容量租约支持的上限。");
        }
        if (currentObjectBytes > _maximumObjectSpoolBytes
            || additionalObjectBytes < 0
            || additionalObjectBytes > _maximumObjectSpoolBytes - currentObjectBytes
            || currentEdgeBytes > _maximumEdgeSpoolBytes
            || additionalEdgeBytes < 0
            || additionalEdgeBytes > _maximumEdgeSpoolBytes - currentEdgeBytes
            || currentRootBytes > _maximumRootSpoolBytes
            || additionalRootBytes < 0
            || additionalRootBytes > _maximumRootSpoolBytes - currentRootBytes)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.SnapshotStorageLimitReached,
                "Profiler 原始记录超过保留快照格式支持的单类记录上限。");
        }
    }

    /// <summary>
    /// 在完成边界后补齐 Finalizing 阶段已转存对象的大小，并写入冻结后的根、函数和类型证据。
    /// </summary>
    private async Task<RetentionProfilerRawCaptureSpool> CompleteSpoolAsync(
        RetentionProfilerRawCaptureSpool spool,
        RetentionProfilerSpoolDrainState drainState,
        CancellationToken cancellationToken)
    {
        var functions = new List<RetentionProfilerRawFunction>();
        var types = new List<RetentionProfilerRawType>();
        await using var objectStream = new FileStream(spool.ObjectPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var rootStream = new FileStream(spool.RootPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var completedHeaders = new RetentionProfilerSharedHeader[_views.Length];
        var functionOffsets = new int[_views.Length];
        for (var segmentIndex = 0; segmentIndex < _views.Length; segmentIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _views[segmentIndex].Read(0, out RetentionProfilerSharedHeader header);
            ValidateCompletedHeader(header, requireCompletedStatus: true);
            completedHeaders[segmentIndex] = header;
            var segmentFunctions = ReadFunctionRecords(_views[segmentIndex], header);
            functionOffsets[segmentIndex] = functions.Count;
            functions.AddRange(segmentFunctions);
            types.AddRange(ReadTypeRecords(_views[segmentIndex], header));
        }

        foreach (var range in drainState.ObjectRanges)
        {
            await PatchObjectSizesAsync(
                _views[range.SegmentIndex],
                completedHeaders[range.SegmentIndex],
                range.SourceRecordIndex,
                range.RecordCount,
                objectStream,
                range.DestinationOffset,
                cancellationToken).ConfigureAwait(false);
        }
        foreach (var range in drainState.RootRanges)
        {
            await PatchRootsAsync(
                _views[range.SegmentIndex],
                completedHeaders[range.SegmentIndex],
                range.SourceRecordIndex,
                range.RecordCount,
                functionOffsets[range.SegmentIndex],
                rootStream,
                range.DestinationOffset,
                cancellationToken).ConfigureAwait(false);
        }

        spool.SetEvidence(functions, types);
        return spool;
    }

    /// <summary>
    /// 将已经冻结的固定宽度对象或边记录按 64 KiB 块从映射复制到 spool。
    /// </summary>
    private static async Task CopyFixedRecordsAsync(
        MemoryMappedViewAccessor view,
        uint offset,
        long byteCount,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var copied = 0L;
        while (copied < byteCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, byteCount - copied);
            view.ReadArray(checked((long)offset + copied), buffer, 0, count);
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            copied += count;
        }
    }

    /// <summary>
    /// 按固定块将已转存对象记录中的大小列补齐为 Completed 阶段的最终值；
    /// 对象标识和 ClassID 在 Finalizing 阶段已经稳定，因此不复制或保留整图 DTO。
    /// </summary>
    private static async Task PatchObjectSizesAsync(
        MemoryMappedViewAccessor view,
        RetentionProfilerSharedHeader header,
        int sourceRecordIndex,
        int recordCount,
        FileStream destination,
        long destinationOffset,
        CancellationToken cancellationToken)
    {
        const int recordsPerChunk = 2_048;
        var sourceBuffer = new byte[recordsPerChunk * ObjectRecordBytes];
        var destinationBuffer = new byte[recordsPerChunk * ObjectRecordBytes];
        var copied = 0;
        while (copied < recordCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(recordsPerChunk, recordCount - copied);
            var byteCount = checked(count * ObjectRecordBytes);
            view.ReadArray(
                checked((long)header.ObjectOffset + (long)(sourceRecordIndex + copied) * ObjectRecordBytes),
                sourceBuffer,
                0,
                byteCount);
            destination.Position = checked(destinationOffset + (long)copied * ObjectRecordBytes);
            await destination.ReadExactlyAsync(destinationBuffer.AsMemory(0, byteCount), cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < count; index++)
            {
                var sizeOffset = index * ObjectRecordBytes + sizeof(ulong) * 2;
                BinaryPrimitives.WriteUInt64LittleEndian(
                    destinationBuffer.AsSpan(sizeOffset, sizeof(ulong)),
                    BinaryPrimitives.ReadUInt64LittleEndian(sourceBuffer.AsSpan(sizeOffset, sizeof(ulong))));
            }

            destination.Position = checked(destinationOffset + (long)copied * ObjectRecordBytes);
            await destination.WriteAsync(destinationBuffer.AsMemory(0, byteCount), cancellationToken).ConfigureAwait(false);
            copied += count;
        }
    }

    /// <summary>
    /// 从最终合法回调已补齐的根 ledger 读取函数证据索引，并在原位修补先前持续转存的根 spool。
    /// </summary>
    private static async Task PatchRootsAsync(
        MemoryMappedViewAccessor view,
        RetentionProfilerSharedHeader header,
        int sourceRecordIndex,
        int recordCount,
        int functionOffset,
        FileStream destination,
        long destinationOffset,
        CancellationToken cancellationToken)
    {
        const int recordsPerChunk = 2_048;
        const int functionEvidenceIndexOffset = sizeof(ulong) + sizeof(uint) * 2 + sizeof(ulong);
        var sourceBuffer = new byte[recordsPerChunk * RootRecordBytes];
        var destinationBuffer = new byte[recordsPerChunk * RootRecordBytes];
        var copied = 0;
        while (copied < recordCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(recordsPerChunk, recordCount - copied);
            var byteCount = checked(count * RootRecordBytes);
            view.ReadArray(
                checked((long)header.RootOffset + (long)(sourceRecordIndex + copied) * RootRecordBytes),
                sourceBuffer,
                0,
                byteCount);
            destination.Position = checked(destinationOffset + (long)copied * RootRecordBytes);
            await destination.ReadExactlyAsync(destinationBuffer.AsMemory(0, byteCount), cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < count; index++)
            {
                var functionIndexOffset = index * RootRecordBytes + functionEvidenceIndexOffset;
                var functionEvidenceIndex = BinaryPrimitives.ReadUInt32LittleEndian(sourceBuffer.AsSpan(functionIndexOffset, sizeof(uint)));
                if (functionEvidenceIndex != uint.MaxValue)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        destinationBuffer.AsSpan(functionIndexOffset, sizeof(uint)),
                        checked(functionEvidenceIndex + (uint)functionOffset));
                }
            }

            destination.Position = checked(destinationOffset + (long)copied * RootRecordBytes);
            await destination.WriteAsync(destinationBuffer.AsMemory(0, byteCount), cancellationToken).ConfigureAwait(false);
            copied += count;
        }
    }

    /// <summary>
    /// 读取映射头部并验证其不会被其他同名对象替换。
    /// </summary>
    private RetentionProfilerSharedHeader ReadHeader()
    {
        _views[0].Read(0, out RetentionProfilerSharedHeader header);
        if (header.Magic != SharedMemoryMagic || header.Version != ProtocolVersion || header.CapacityBytes != SegmentCapacityBytes)
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.ProfilerCaptureFailed, "原生保留 Profiler 共享内存协议无效。");
        }
        return header;
    }

    /// <summary>
    /// 将共享头部记录的首个原生失败 HRESULT 转为稳定诊断错误，便于区分容量耗尽和 CLR 调用序列限制。
    /// </summary>
    private static DiagnosticsException CreateNativeCaptureFailure(RetentionProfilerSharedHeader header)
    {
        const int outOfMemory = unchecked((int)0x8007000E);
        var errorCode = header.FailureHResult == outOfMemory
            ? DiagnosticsErrorCode.ProfilerCaptureBufferExhausted
            : DiagnosticsErrorCode.ProfilerCaptureFailed;
        var message = errorCode is DiagnosticsErrorCode.ProfilerCaptureBufferExhausted
            ? "原生保留 Profiler 的预分配记录缓冲区已耗尽，快照未被保存。"
            : $"原生保留 Profiler 报告捕获失败 (HRESULT 0x{header.FailureHResult:X8})。";
        return new DiagnosticsException(errorCode, message);
    }

    /// <summary>
    /// 先预留有界函数与类型证据，再以 27% 对象、54% 边和剩余空间为根记录划分对象图区域。
    /// </summary>
    private static RetentionProfilerSharedLayout CreateLayout(uint capacityBytes)
    {
        var available = checked((int)capacityBytes - HeaderBytes);
        var functionCapacity = Math.Min(MaximumFunctionEvidence, available / 10 / FunctionRecordBytes);
        var functionBytes = checked(functionCapacity * FunctionRecordBytes);
        var typeCapacity = Math.Min(MaximumTypeEvidence, available / 16 / TypeRecordBytes);
        var typeBytes = checked(typeCapacity * TypeRecordBytes);
        var graphBytes = available - functionBytes - typeBytes;
        var objectBytes = AlignDown(checked((int)((long)graphBytes * 27 / 100)), 8);
        var edgeBytes = AlignDown(checked((int)((long)graphBytes * 54 / 100)), 8);
        var rootBytes = AlignDown(graphBytes - objectBytes - edgeBytes, 8);
        var functionOffset = HeaderBytes;
        var typeOffset = checked(functionOffset + functionBytes);
        var objectOffset = checked(typeOffset + typeBytes);
        var edgeOffset = checked(objectOffset + objectBytes);
        var rootOffset = checked(edgeOffset + edgeBytes);
        return new RetentionProfilerSharedLayout(
            checked((uint)objectOffset),
            checked((uint)(objectBytes / ObjectRecordBytes)),
            checked((uint)edgeOffset),
            checked((uint)(edgeBytes / EdgeRecordBytes)),
            checked((uint)rootOffset),
            checked((uint)(rootBytes / RootRecordBytes)),
            checked((uint)functionOffset),
            checked((uint)functionCapacity),
            checked((uint)typeOffset),
            checked((uint)typeCapacity));
    }

    /// <summary>
    /// 向下对齐以保持 C++ 结构数组可安全访问。
    /// </summary>
    private static int AlignDown(int value, int alignment) => value - value % alignment;

    /// <summary>
    /// 释放命名映射和事件，不等待目标进程；Profiler 随 CLR 分离而关闭其自身句柄。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _failureEvent.Dispose();
        _completionEvent.Dispose();
        _detachEvent.Dispose();
        foreach (var view in _views)
        {
            view.Dispose();
        }
        foreach (var mapping in _mappings)
        {
            mapping.Dispose();
        }
    }

    /// <summary>
    /// 跟踪持续转存的下一发布序列、每段 ledger 水位及磁盘范围，以便完成阶段原位回填而不重建托管对象图。
    /// </summary>
    private sealed class RetentionProfilerSpoolDrainState
    {
        /// <summary>
        /// 为所有预分配共享段创建转存状态。
        /// </summary>
        /// <param name="segmentCount">需要记录对象 spool 起始偏移的共享段数量。</param>
        public RetentionProfilerSpoolDrainState(int segmentCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentCount);
            DrainedObjectCounts = new int[segmentCount];
            DrainedRootCounts = new int[segmentCount];
        }

        /// <summary>每个物理段已经写入对象 spool 的只追加 ledger 水位。</summary>
        public int[] DrainedObjectCounts { get; }

        /// <summary>每个物理段已经写入根 spool 的只追加 ledger 水位。</summary>
        public int[] DrainedRootCounts { get; }

        /// <summary>等待消费的下一全局发布序列，初始为一且只在完整确认后递增。</summary>
        public long NextPublicationSequence { get; set; } = 1;

        /// <summary>已经持续转存的对象磁盘范围，用于最终合法阶段仅回填大小列。</summary>
        public List<RetentionProfilerPublishedRange> ObjectRanges { get; } = [];

        /// <summary>已经持续转存的根磁盘范围，用于最终合法阶段回填函数证据索引。</summary>
        public List<RetentionProfilerPublishedRange> RootRanges { get; } = [];
    }

    /// <summary>
    /// 描述一次发布中从物理段 ledger 转存到 spool 的连续固定宽度范围。
    /// </summary>
    private readonly record struct RetentionProfilerPublishedRange(
        int SegmentIndex,
        int SourceRecordIndex,
        int RecordCount,
        long DestinationOffset);

    /// <summary>
    /// 防止资源释放后再次访问命名对象。
    /// </summary>
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>
/// 与原生 AttachProfiler 客户端数据严格对应的固定布局；三个字符串均为 260 个 UTF-16 字符的缓冲区。
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct RetentionProfilerAttachData
{
    /// <summary>协议版本。</summary>
    public uint Version;

    /// <summary>单个预分配共享段容量。</summary>
    public uint SegmentCapacityBytes;

    /// <summary>预分配共享段数量，范围为 2 至 4。</summary>
    public uint SegmentCount;

    /// <summary>第一段命名共享内存名称。</summary>
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string SegmentName0;

    /// <summary>第二段命名共享内存名称。</summary>
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string SegmentName1;

    /// <summary>第三段命名共享内存名称；未使用时为空字符串。</summary>
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string SegmentName2;

    /// <summary>第四段命名共享内存名称；未使用时为空字符串。</summary>
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string SegmentName3;

    /// <summary>成功完成事件名称。</summary>
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string CompletionEventName;

    /// <summary>失败事件名称。</summary>
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string FailureEventName;

    /// <summary>CLR 确认 Profiler 分离后设置的事件名称。</summary>
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string DetachEventName;
}

/// <summary>
/// 映射头部的托管镜像，字段偏移与 Windows x64 原生结构一致。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RetentionProfilerSharedHeader
{
    public uint Magic;
    public uint Version;
    public uint CapacityBytes;
    public int Status;
    public uint ObjectOffset;
    public uint ObjectCapacity;
    public int ObjectCount;
    public uint EdgeOffset;
    public uint EdgeCapacity;
    public int EdgeCount;
    public uint RootOffset;
    public uint RootCapacity;
    public int RootCount;
    public uint FunctionOffset;
    public uint FunctionCapacity;
    public int FunctionCount;

    /// <summary>类型证据记录区的偏移、容量和已验证记录数。</summary>
    public uint TypeOffset;
    public uint TypeCapacity;
    public int TypeCount;

    /// <summary>原生 Profiler 首个失败 HRESULT；S_OK 表示尚未记录失败。</summary>
    public int FailureHResult;
    public long LastProgressTickCount;

    /// <summary>单个共享段的生产者/消费者生命周期。</summary>
    public int SegmentState;

    /// <summary>低 31 位是当前代未退出的 writer 数；符号位表示 publisher 已关闭新登记。</summary>
    public int ActiveWriterCount;

    /// <summary>当前已发布代的全局单调序列。</summary>
    public long PublicationSequence;

    /// <summary>Controller 最后完整转存并确认的发布序列。</summary>
    public long AcknowledgedSequence;
}

/// <summary>
/// 单个 v6 共享段的原生生产者/受管消费者生命周期镜像。
/// </summary>
internal enum RetentionProfilerSegmentState
{
    Reusable,
    Initializing,
    Writing,
    AssigningSequence,
    Sealing,
    Published
}

/// <summary>
/// 原生共享内存采集状态的托管镜像。
/// </summary>
internal enum RetentionProfilerCaptureStatus
{
    Pending,
    Capturing,
    Finalizing,
    Completed,
    Failed
}

/// <summary>
/// 原生 ObjectReferences 记录的托管镜像。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RetentionProfilerRawObject
{
    public nuint ObjectId;
    public nuint ClassId;
    public nuint SizeBytes;

    /// <summary>创建一条已冻结的对象记录，并保留 CLR 报告的对象大小。</summary>
    public RetentionProfilerRawObject(nuint objectId, nuint classId, nuint sizeBytes = 0) =>
        (ObjectId, ClassId, SizeBytes) = (objectId, classId, sizeBytes);
}

/// <summary>
/// 原生对象引用边记录的托管镜像。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RetentionProfilerRawEdge
{
    public nuint SourceObjectId;
    public nuint TargetObjectId;

    /// <summary>创建一条已冻结的对象引用边。</summary>
    public RetentionProfilerRawEdge(nuint sourceObjectId, nuint targetObjectId) =>
        (SourceObjectId, TargetObjectId) = (sourceObjectId, targetObjectId);
}

/// <summary>
/// 原生 RootReferences2 记录的托管镜像；栈根 RootId 是未经伪造的 FunctionID。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RetentionProfilerRawRoot
{
    public nuint ObjectId;
    public uint RootKind;
    public uint RootFlags;
    public nuint RootId;
    public uint FunctionEvidenceIndex;
    public uint Reserved;

    /// <summary>创建一条保留 CLR 原始根证据的已冻结记录。</summary>
    public RetentionProfilerRawRoot(
        nuint objectId,
        uint rootKind,
        uint rootFlags,
        nuint rootId,
        uint functionEvidenceIndex,
        uint reserved = 0) =>
        (ObjectId, RootKind, RootFlags, RootId, FunctionEvidenceIndex, Reserved) =
        (objectId, rootKind, rootFlags, rootId, functionEvidenceIndex, reserved);
}

/// <summary>
/// 类型证据原生记录的托管 ABI 镜像；字符串只在共享内存读取阶段按固定 UTF-16 缓冲区解析。
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct RetentionProfilerTypeEvidenceRecord
{
    /// <summary>实际 CLR ClassID。</summary>
    public nuint ClassId;

    /// <summary>固定 512 个 UTF-16 字符的类型名缓冲区。</summary>
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
    public string TypeName;

    /// <summary>固定 260 个 UTF-16 字符的模块名缓冲区。</summary>
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string ModuleName;
}

/// <summary>
/// 表示一次已冻结的原始对象、边和根数据，所有记录均由共享内存复制而来。
/// </summary>
internal sealed record RetentionProfilerRawCapture(
    IReadOnlyList<RetentionProfilerRawObject> Objects,
    IReadOnlyList<RetentionProfilerRawEdge> Edges,
    IReadOnlyList<RetentionProfilerRawRoot> Roots,
    IReadOnlyList<RetentionProfilerRawFunction> Functions,
    IReadOnlyList<RetentionProfilerRawType> Types)
{
    /// <summary>创建没有类型证据的兼容原始捕获，供旧调用方逐步迁移。</summary>
    public RetentionProfilerRawCapture(
        IReadOnlyList<RetentionProfilerRawObject> objects,
        IReadOnlyList<RetentionProfilerRawEdge> edges,
        IReadOnlyList<RetentionProfilerRawRoot> roots,
        IReadOnlyList<RetentionProfilerRawFunction> functions)
        : this(objects, edges, roots, functions, Array.Empty<RetentionProfilerRawType>())
    {
    }
}

/// <summary>
/// 表示一个经 CLR Metadata API 验证的栈根函数及其模块名。
/// </summary>
internal readonly record struct RetentionProfilerRawFunction(nuint FunctionId, string? FunctionName, string? ModuleName);

/// <summary>
/// 表示 CLR Metadata API 从 ClassID 验证得到的类型名和定义模块；未验证的 ClassID 不产生该记录。
/// </summary>
internal readonly record struct RetentionProfilerRawType(nuint ClassId, string? TypeName, string? ModuleName);

/// <summary>
/// 表示共享内存三个记录区的偏移和容量。
/// </summary>
internal readonly record struct RetentionProfilerSharedLayout(
    uint ObjectOffset,
    uint ObjectCapacity,
    uint EdgeOffset,
    uint EdgeCapacity,
    uint RootOffset,
    uint RootCapacity,
    uint FunctionOffset,
    uint FunctionCapacity,
    uint TypeOffset,
    uint TypeCapacity);
