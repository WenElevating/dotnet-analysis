using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using System.Text;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 将 EventPipe GC 堆事件顺序写入调用专属固定宽度文件；仅类型元数据按类型数量保留在托管内存中，
/// 对象、边与根的规模不会转化为常驻集合。
/// </summary>
internal sealed class EventPipeHeapSpool : IDisposable
{
    internal const int ObjectRecordBytes = sizeof(ulong) + sizeof(ulong) + sizeof(long) + sizeof(long);
    internal const int EdgeRecordBytes = sizeof(ulong);
    internal const int RootRecordBytes = sizeof(ulong) + sizeof(int);

    private const int MaximumTypeCount = 10_000_000;
    private readonly Dictionary<ulong, TypeIdentity> _types = [];
    private readonly string _workingDirectory;
    private readonly string _objectPath;
    private readonly string _edgePath;
    private readonly string _rootPath;
    private readonly CancellationToken _captureCancellationToken;
    private BinaryWriter? _objects;
    private BinaryWriter? _edges;
    private BinaryWriter? _roots;
    private long _objectCount;
    private long _declaredEdgeCount;
    private long _edgeCount;
    private long _rootCount;
    private bool _sealed;
    private bool _disposed;

    /// <summary>
    /// 打开本次 EventPipe 解析独占的对象、边和根 spool；目录会在释放时递归清理。
    /// </summary>
    private EventPipeHeapSpool(string workingDirectory, CancellationToken captureCancellationToken)
    {
        _workingDirectory = workingDirectory;
        _captureCancellationToken = captureCancellationToken;
        _objectPath = Path.Combine(workingDirectory, "objects.raw.bin");
        _edgePath = Path.Combine(workingDirectory, "edge-targets.raw.bin");
        _rootPath = Path.Combine(workingDirectory, "roots.raw.bin");
        Directory.CreateDirectory(workingDirectory);
        try
        {
            _objects = CreateWriter(_objectPath);
            _edges = CreateWriter(_edgePath);
            _roots = CreateWriter(_rootPath);
        }
        catch
        {
            DisposeWriters();
            HeapTemporaryArtifactCleanup.TryDeleteDirectory(_workingDirectory);
            throw;
        }
    }

    /// <summary>
    /// 已接收且写入 spool 的有效对象数；用于完成解析后按实际图规模路由。
    /// </summary>
    internal long ObjectCount => _objectCount;

    /// <summary>
    /// 已接收的边目标记录数；完整性校验要求它与所有节点声明的边数相等。
    /// </summary>
    internal long EdgeCount => _edgeCount;

    /// <summary>
    /// 已接收的非零 GC 根记录数；重复根由后续索引构建稳定去重。
    /// </summary>
    internal long RootCount => _rootCount;

    /// <summary>
    /// 本次调用专属的工作目录，仅供索引发布和损坏输入测试使用。
    /// </summary>
    internal string WorkingDirectory => _workingDirectory;

    /// <summary>
    /// 在指定父目录创建唯一工作区；创建失败不会修改原始 .gcdump。
    /// </summary>
    /// <param name="parentDirectory">允许创建本次 spool 子目录的受管目录。</param>
    /// <param name="cancellationToken">解析期间共享的取消令牌。</param>
    /// <returns>拥有独占工作目录和文件句柄的 spool。</returns>
    internal static Task<EventPipeHeapSpool> CreateAsync(
        string parentDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        Directory.CreateDirectory(parentDirectory);
        HeapTemporaryArtifactCleanup.TryDeleteAbandonedDirectories(
            parentDirectory,
            ".eventpipe-heap-spool.*.tmp",
            HeapTemporaryArtifactCleanup.AbandonedDirectoryMinimumAge);
        var workingDirectory = Path.Combine(parentDirectory, $".eventpipe-heap-spool.{Guid.NewGuid():N}.tmp");
        return Task.FromResult(new EventPipeHeapSpool(workingDirectory, cancellationToken));
    }

    /// <summary>
    /// 将运行时类型标识关联到最终显示名；节点可在类型事件之前写入，构建阶段才解析身份。
    /// </summary>
    /// <param name="typeId">EventPipe 进程内类型标识。</param>
    /// <param name="typeName">运行时类型名；空白值使用稳定十六进制占位名。</param>
    /// <param name="assemblyName">可用时的程序集名；GC bulk type 当前通常不提供。</param>
    internal void RecordType(ulong typeId, string? typeName, string? assemblyName = null)
    {
        EnsureWritable();
        if (!_types.ContainsKey(typeId) && _types.Count >= MaximumTypeCount)
        {
            throw new InvalidDataException("EventPipe 类型数量超过索引格式上限。");
        }

        _types[typeId] = new TypeIdentity(
            string.IsNullOrWhiteSpace(typeName) ? $"Type(0x{typeId:x})" : typeName,
            string.IsNullOrWhiteSpace(assemblyName) ? null : assemblyName);
    }

    /// <summary>
    /// 写入一条对象记录及其随后应从边流消费的槽位数；无效大小或计数直接失败，避免错位后伪造完整图。
    /// </summary>
    /// <param name="address">对象地址。</param>
    /// <param name="typeId">运行时类型标识。</param>
    /// <param name="sizeBytes">对象浅表大小。</param>
    /// <param name="edgeCount">紧随节点序列出现的边目标数量。</param>
    internal void RecordNode(ulong address, ulong typeId, ulong sizeBytes, long edgeCount)
    {
        EnsureWritable();
        if (sizeBytes > long.MaxValue || edgeCount < 0)
        {
            throw new InvalidDataException("EventPipe 对象大小或边数量无效。");
        }

        _captureCancellationToken.ThrowIfCancellationRequested();
        _objects!.Write(address);
        _objects.Write(typeId);
        _objects.Write((long)sizeBytes);
        _objects.Write(edgeCount);
        _objectCount = checked(_objectCount + 1);
        _declaredEdgeCount = checked(_declaredEdgeCount + edgeCount);
    }

    /// <summary>
    /// 按 EventPipe GCBulkEdge 顺序写入一个目标地址；源对象由节点声明的边槽位顺序恢复。
    /// </summary>
    /// <param name="targetAddress">被引用对象地址，零地址在构建时忽略但仍占用声明槽位。</param>
    internal void RecordEdgeTarget(ulong targetAddress)
    {
        EnsureWritable();
        _captureCancellationToken.ThrowIfCancellationRequested();
        _edges!.Write(targetAddress);
        _edgeCount = checked(_edgeCount + 1);
    }

    /// <summary>
    /// 写入一个非零 GC 根对象地址；EventPipe 根不携带可信函数证据，后续固定投影为 Unknown。
    /// </summary>
    /// <param name="objectAddress">根直接引用的对象地址。</param>
    /// <param name="flags">EventPipe 可证明的根标志；当前只持久化影响存活语义的弱根位。</param>
    internal void RecordRoot(ulong objectAddress, MemoryRootFlags flags = MemoryRootFlags.None)
    {
        EnsureWritable();
        _captureCancellationToken.ThrowIfCancellationRequested();
        if (objectAddress == 0)
        {
            return;
        }

        _roots!.Write(objectAddress);
        _roots.Write((int)(flags & MemoryRootFlags.WeakReference));
        _rootCount = checked(_rootCount + 1);
    }

    /// <summary>
    /// 把固定宽度记录重新投影为小图常驻索引；该入口只应在路由器确认资源预算允许后调用。
    /// </summary>
    /// <param name="cancellationToken">取消当前读取与投影。</param>
    /// <returns>保持旧 EventPipe 对象、路径和未知根语义的紧凑索引。</returns>
    internal SnapshotIndex BuildInMemoryIndex(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SealAndValidate();
            if (_objectCount == 0)
            {
                throw new InvalidDataException("The gcdump did not contain heap object events.");
            }

            var builder = new SnapshotIndexBuilder();
            var objectAddresses = new HashSet<ulong>();
            using (var objectReader = OpenReader(_objectPath))
            {
                for (long objectIndex = 0; objectIndex < _objectCount; objectIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = ReadObject(objectReader);
                    if (!objectAddresses.Add(row.Address))
                    {
                        throw new InvalidDataException("EventPipe 对象地址重复。");
                    }

                    builder.AddObject(row.Address, ResolveType(row.TypeId), row.SizeBytes);
                }
            }

            using (var objectReader = OpenReader(_objectPath))
            using (var edgeReader = OpenReader(_edgePath))
            {
                for (long objectIndex = 0; objectIndex < _objectCount; objectIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = ReadObject(objectReader);
                    if (row.EdgeCount == 0)
                    {
                        continue;
                    }

                    var targets = new ulong[checked((int)row.EdgeCount)];
                    for (var edgeIndex = 0; edgeIndex < targets.Length; edgeIndex++)
                    {
                        targets[edgeIndex] = edgeReader.ReadUInt64();
                        if (targets[edgeIndex] != 0 && !objectAddresses.Contains(targets[edgeIndex]))
                        {
                            throw new InvalidDataException("EventPipe 引用边包含未知非零目标地址。");
                        }
                    }

                    builder.AddEdges(row.Address, targets);
                }
            }

            using (var rootReader = OpenReader(_rootPath))
            {
                for (long rootIndex = 0; rootIndex < _rootCount; rootIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var address = rootReader.ReadUInt64();
                    var flags = (MemoryRootFlags)rootReader.ReadInt32();
                    builder.AddRoot(
                        address,
                        new MemoryRetentionRoot(MemoryRootKind.Unknown, flags, null, null));
                }
            }

            return builder.Build();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DiagnosticsException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException or OverflowException or ArgumentException)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.SnapshotIndexBuildFailed,
                "无法从 EventPipe spool 构建内存堆索引。",
                exception);
        }
    }

    /// <summary>
    /// 从固定宽度 EventPipe spool 直接写出 v3 基础索引的全部工件；对象地址和边端点通过外排归并转换，
    /// 不构造完整对象 DTO、地址字典或边集合。
    /// </summary>
    /// <param name="directory">原子发布器提供的空临时目录。</param>
    /// <param name="cancellationToken">取消本次工件构建；取消不会被包装为稳定失败。</param>
    /// <returns>精确包含 v3 必需工件的相对文件名。</returns>
    internal async Task<IReadOnlyList<string>> WriteMappedArtifactsAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(directory);
            SealAndValidate();
            if (_objectCount == 0 || _objectCount > int.MaxValue)
            {
                throw new InvalidDataException("EventPipe 对象数量为空或超过索引格式上限。");
            }

            Directory.CreateDirectory(directory);
            var writer = new EventPipeHeapArtifactWriter(
                directory,
                _objectPath,
                _edgePath,
                _rootPath,
                checked((int)_objectCount),
                _edgeCount,
                _rootCount,
                _types);
            return await writer.WriteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DiagnosticsException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException or OverflowException or ArgumentException or System.Text.Json.JsonException)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.SnapshotIndexBuildFailed,
                "无法从 EventPipe spool 构建磁盘堆索引。",
                exception);
        }
    }

    /// <summary>
    /// 订阅 EventPipe GC 堆事件，并把回调中的批次立即投影为固定宽度记录。
    /// </summary>
    /// <param name="source">正在读取原始 EventPipe 文件的事件源。</param>
    internal void Attach(EventPipeEventSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.Clr.TypeBulkType += data =>
        {
            for (var index = 0; index < data.Count; index++)
            {
                var value = data.Values(index);
                RecordType(
                    value.TypeID,
                    string.IsNullOrWhiteSpace(value.TypeName)
                        ? $"Type(0x{value.TypeNameID:x})"
                        : value.TypeName);
            }
        };
        source.Clr.GCBulkNode += data =>
        {
            for (var index = 0; index < data.Count; index++)
            {
                var value = data.Values(index);
                RecordNode(value.Address, value.TypeID, value.Size, value.EdgeCount);
            }
        };
        source.Clr.GCBulkEdge += data =>
        {
            for (var index = 0; index < data.Count; index++)
            {
                RecordEdgeTarget(data.Values(index).Target);
            }
        };
        source.Clr.GCBulkRootEdge += data =>
        {
            for (var index = 0; index < data.Count; index++)
            {
                var value = data.Values(index);
                var flags = (value.GCRootFlag & GCRootFlags.WeakRef) != 0
                    ? MemoryRootFlags.WeakReference
                    : MemoryRootFlags.None;
                RecordRoot(value.RootedNodeAddress, flags);
            }
        };
    }

    /// <summary>
    /// 关闭文件句柄并删除本次调用的工作目录；已发布索引和原始 .gcdump 不在此目录内。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeWriters();
        HeapTemporaryArtifactCleanup.TryDeleteDirectory(_workingDirectory);
    }

    /// <summary>
    /// 创建适合 EventPipe 同步回调持续追加的顺序写入器；缓冲由 FileStream 控制而非托管记录集合。
    /// </summary>
    private static BinaryWriter CreateWriter(string path) => new(
        new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.SequentialScan),
        Encoding.UTF8,
        leaveOpen: false);

    /// <summary>
    /// 以顺序扫描方式打开一个只读固定宽度 spool。
    /// </summary>
    private static BinaryReader OpenReader(string path) => new(
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan),
        Encoding.UTF8,
        leaveOpen: false);

    /// <summary>
    /// 冻结写端并校验所有固定宽度文件和节点声明的边槽位，拒绝截断或多余边数据。
    /// </summary>
    private void SealAndValidate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_sealed)
        {
            _sealed = true;
            DisposeWriters();
        }

        if (new FileInfo(_objectPath).Length != checked(_objectCount * ObjectRecordBytes)
            || new FileInfo(_edgePath).Length != checked(_edgeCount * EdgeRecordBytes)
            || new FileInfo(_rootPath).Length != checked(_rootCount * RootRecordBytes)
            || _declaredEdgeCount != _edgeCount)
        {
            throw new InvalidDataException("EventPipe 固定宽度 spool 截断或边数量与节点声明不一致。");
        }
    }

    /// <summary>
    /// 释放三个追加写入器；重复调用用于 Seal 与 Dispose 共用清理路径。
    /// </summary>
    private void DisposeWriters()
    {
        _objects?.Dispose();
        _objects = null;
        _edges?.Dispose();
        _edges = null;
        _roots?.Dispose();
        _roots = null;
    }

    /// <summary>
    /// 保证事件只能在 Seal 或释放之前写入，避免生成计数与文件长度不一致的图。
    /// </summary>
    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sealed)
        {
            throw new InvalidOperationException("EventPipe heap spool 已冻结。");
        }
    }

    /// <summary>
    /// 读取一条完整对象记录；调用方已验证文件总长度，EOF 仍作为损坏输入处理。
    /// </summary>
    private static EventPipeObjectRecord ReadObject(BinaryReader reader) => new(
        reader.ReadUInt64(),
        reader.ReadUInt64(),
        reader.ReadInt64(),
        reader.ReadInt64());

    /// <summary>
    /// 在最终类型表中解析类型标识；从未出现类型元数据时保留稳定占位名。
    /// </summary>
    private TypeIdentity ResolveType(ulong typeId) => _types.TryGetValue(typeId, out var type)
        ? type
        : new TypeIdentity($"Type(0x{typeId:x})", null);

    /// <summary>
    /// 表示 spool 中一条固定宽度对象记录及其顺序边槽位数量。
    /// </summary>
    private readonly record struct EventPipeObjectRecord(
        ulong Address,
        ulong TypeId,
        long SizeBytes,
        long EdgeCount);
}
