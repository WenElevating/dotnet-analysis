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
/// 本类型在完成事件建立 happens-before 边界后才读取结果。
/// </remarks>
internal sealed class RetentionProfilerCaptureSession : IDisposable
{
    private const uint ProtocolVersion = 5;
    private const uint SharedMemoryMagic = 0x50415244;
    private const int MinimumMappingBytes = 64 * 1024;
    private const int DefaultMappingBytes = 256 * 1024 * 1024;
    private const int ObjectRecordBytes = 24;
    private const int EdgeRecordBytes = 16;
    private const int RootRecordBytes = 32;
    private const int FunctionRecordBytes = 1_552;
    private const int TypeRecordBytes = 1_552;
    private const int HeaderBytes = 88;
    private const int MaximumFunctionEvidence = 1_024;
    private const int MaximumTypeEvidence = 4_096;
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private readonly EventWaitHandle _completionEvent;
    private readonly EventWaitHandle _failureEvent;
    private readonly EventWaitHandle _detachEvent;
    private bool _disposed;

    /// <summary>
    /// 创建一个已初始化且仅供本次附加使用的会话。
    /// </summary>
    /// <param name="mappingCapacityBytes">共享映射总容量；未指定时使用 256 MiB，以降低完整诊断会话中对象图记录溢出的概率。</param>
    public RetentionProfilerCaptureSession(int mappingCapacityBytes = DefaultMappingBytes)
    {
        if (mappingCapacityBytes < MinimumMappingBytes || mappingCapacityBytes > int.MaxValue - HeaderBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(mappingCapacityBytes));
        }

        var token = Guid.NewGuid().ToString("N");
        MappingName = $"DotnetAnalysis.Retention.{token}.Mapping";
        CompletionEventName = $"DotnetAnalysis.Retention.{token}.Complete";
        FailureEventName = $"DotnetAnalysis.Retention.{token}.Failed";
        DetachEventName = $"DotnetAnalysis.Retention.{token}.Detached";
        MappingCapacityBytes = checked((uint)mappingCapacityBytes);
        _mapping = MemoryMappedFile.CreateNew(MappingName, mappingCapacityBytes, MemoryMappedFileAccess.ReadWrite);
        _view = _mapping.CreateViewAccessor(0, mappingCapacityBytes, MemoryMappedFileAccess.ReadWrite);
        _completionEvent = new EventWaitHandle(false, EventResetMode.ManualReset, CompletionEventName);
        _failureEvent = new EventWaitHandle(false, EventResetMode.ManualReset, FailureEventName);
        _detachEvent = new EventWaitHandle(false, EventResetMode.ManualReset, DetachEventName);

        var layout = CreateLayout(MappingCapacityBytes);
        var header = new RetentionProfilerSharedHeader
        {
            Magic = SharedMemoryMagic,
            Version = ProtocolVersion,
            CapacityBytes = MappingCapacityBytes,
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
            LastProgressTickCount = Environment.TickCount64
        };
        _view.Write(0, ref header);
    }

    /// <summary>
    /// 原生 Profiler 要打开的命名共享内存对象。
    /// </summary>
    public string MappingName { get; }

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
    /// 共享内存总容量。
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
            MappingCapacityBytes = MappingCapacityBytes,
            MappingName = MappingName,
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
        var header = ReadHeader();
        if ((RetentionProfilerCaptureStatus)header.Status is not RetentionProfilerCaptureStatus.Completed
            || header.ObjectCount < 0 || header.EdgeCount < 0 || header.RootCount < 0
            || header.FunctionCount < 0
            || header.TypeCount < 0
            || (uint)header.ObjectCount > header.ObjectCapacity
            || (uint)header.EdgeCount > header.EdgeCapacity
            || (uint)header.RootCount > header.RootCapacity
            || (uint)header.FunctionCount > header.FunctionCapacity
            || (uint)header.TypeCount > header.TypeCapacity)
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.ProfilerCaptureFailed, "原生保留 Profiler 返回了不完整或越界的共享内存数据。");
        }

        var objects = new RetentionProfilerRawObject[header.ObjectCount];
        var edges = new RetentionProfilerRawEdge[header.EdgeCount];
        var roots = new RetentionProfilerRawRoot[header.RootCount];
        var functions = ReadFunctionRecords(header);
        var types = ReadTypeRecords(header);
        ReadRecords(header.ObjectOffset, objects);
        ReadRecords(header.EdgeOffset, edges);
        ReadRecords(header.RootOffset, roots);
        return new RetentionProfilerRawCapture(objects, edges, roots, functions, types);
    }

    /// <summary>
    /// 从映射内固定位置读取非托管记录数组。
    /// </summary>
    private void ReadRecords<T>(uint offset, T[] records)
        where T : struct
    {
        if (records.Length != 0)
        {
            _view.ReadArray(offset, records, 0, records.Length);
        }
    }

    /// <summary>
    /// 读取固定 UTF-16 函数证据区；只有 CLR 成功解析的栈根才会有非空名称。
    /// </summary>
    private RetentionProfilerRawFunction[] ReadFunctionRecords(RetentionProfilerSharedHeader header)
    {
        var functions = new RetentionProfilerRawFunction[header.FunctionCount];
        if (functions.Length == 0)
        {
            return functions;
        }

        var bytes = new byte[checked(functions.Length * FunctionRecordBytes)];
        _view.ReadArray(header.FunctionOffset, bytes, 0, bytes.Length);
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
    private RetentionProfilerRawType[] ReadTypeRecords(RetentionProfilerSharedHeader header)
    {
        var types = new RetentionProfilerRawType[header.TypeCount];
        if (types.Length == 0)
        {
            return types;
        }

        var bytes = new byte[checked(types.Length * TypeRecordBytes)];
        _view.ReadArray(header.TypeOffset, bytes, 0, bytes.Length);
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
    /// 读取映射头部并验证其不会被其他同名对象替换。
    /// </summary>
    private RetentionProfilerSharedHeader ReadHeader()
    {
        _view.Read(0, out RetentionProfilerSharedHeader header);
        if (header.Magic != SharedMemoryMagic || header.Version != ProtocolVersion || header.CapacityBytes != MappingCapacityBytes)
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.ProfilerCaptureFailed, "原生保留 Profiler 共享内存协议无效。");
        }
        return header;
    }

    /// <summary>
    /// 将共享头部记录的首个原生失败 HRESULT 转为稳定诊断错误，便于区分容量耗尽和 CLR 调用序列限制。
    /// </summary>
    private static DiagnosticsException CreateNativeCaptureFailure(RetentionProfilerSharedHeader header) => new(
        DiagnosticsErrorCode.ProfilerCaptureFailed,
        $"原生保留 Profiler 报告捕获失败 (HRESULT 0x{header.FailureHResult:X8})。");

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
        _view.Dispose();
        _mapping.Dispose();
    }

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

    /// <summary>共享内存总容量。</summary>
    public uint MappingCapacityBytes;

    /// <summary>命名共享内存名称。</summary>
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string MappingName;

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
}

/// <summary>
/// 原生共享内存采集状态的托管镜像。
/// </summary>
internal enum RetentionProfilerCaptureStatus
{
    Pending,
    Capturing,
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
