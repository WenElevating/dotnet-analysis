using System.Buffers.Binary;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 保存一次保留 Profiler 捕获的原始固定宽度记录；对象、边和根始终驻留在临时文件中，
/// 只有协议限定数量的函数和类型证据保留在托管内存。
/// </summary>
/// <remarks>
/// 此类型默认把未成功发布的目录改名归档为原始证据；只有调用方在最终快照已原子提升后显式确认，
/// 释放操作才会删除 spool，避免转换、容量或提升失败销毁唯一原始记录。
/// </remarks>
internal sealed class RetentionProfilerRawCaptureSpool : IDisposable
{
    /// <summary>固定宽度原始对象记录的字节数。</summary>
    public const int ObjectRecordBytes = sizeof(ulong) * 3;

    /// <summary>固定宽度原始引用边记录的字节数。</summary>
    public const int EdgeRecordBytes = sizeof(ulong) * 2;

    /// <summary>固定宽度原始 GC 根记录的字节数。</summary>
    public const int RootRecordBytes = sizeof(ulong) + sizeof(uint) + sizeof(uint) + sizeof(ulong) + sizeof(uint) + sizeof(uint);

    /// <summary>保留快照格式支持的最大对象记录数。</summary>
    internal const int MaximumObjectCount = 100_000_000;

    /// <summary>保留快照格式支持的最大引用边记录数。</summary>
    internal const int MaximumEdgeCount = 500_000_000;

    /// <summary>保留快照格式支持的最大 GC 根记录数。</summary>
    internal const int MaximumRootCount = 100_000_000;

    /// <summary>达到格式记录数上限时对象 raw 文件的最大字节数。</summary>
    internal const long MaximumObjectBytes = (long)MaximumObjectCount * ObjectRecordBytes;

    /// <summary>达到格式记录数上限时引用边 raw 文件的最大字节数。</summary>
    internal const long MaximumEdgeBytes = (long)MaximumEdgeCount * EdgeRecordBytes;

    /// <summary>达到格式记录数上限时 GC 根 raw 文件的最大字节数。</summary>
    internal const long MaximumRootBytes = (long)MaximumRootCount * RootRecordBytes;

    /// <summary>达到格式记录数上限时三个 raw 文件合计的最大字节数。</summary>
    internal const long MaximumSupportedBytes = MaximumObjectBytes
        + MaximumEdgeBytes
        + MaximumRootBytes;

    private bool _disposed;
    private bool _deleteOnDispose;

    /// <summary>
    /// 使用已经创建的、仅属于本次捕获的目录创建原始记录 spool。
    /// </summary>
    private RetentionProfilerRawCaptureSpool(
        string directory,
        IReadOnlyList<RetentionProfilerRawFunction> functions,
        IReadOnlyList<RetentionProfilerRawType> types)
    {
        DirectoryPath = directory;
        Functions = functions;
        Types = types;
    }

    /// <summary>本次捕获专用的临时目录。</summary>
    public string DirectoryPath { get; private set; }

    /// <summary>原始对象记录文件。</summary>
    public string ObjectPath => Path.Combine(DirectoryPath, "objects.raw.bin");

    /// <summary>原始引用边记录文件。</summary>
    public string EdgePath => Path.Combine(DirectoryPath, "edges.raw.bin");

    /// <summary>原始 GC 根记录文件。</summary>
    public string RootPath => Path.Combine(DirectoryPath, "roots.raw.bin");

    /// <summary>已冻结且数量受协议限制的函数证据。</summary>
    public IReadOnlyList<RetentionProfilerRawFunction> Functions { get; private set; }

    /// <summary>已冻结且数量受协议限制的类型证据。</summary>
    public IReadOnlyList<RetentionProfilerRawType> Types { get; private set; }

    /// <summary>原始对象记录数量。</summary>
    public long ObjectCount => GetRecordCount(ObjectPath, ObjectRecordBytes);

    /// <summary>原始边记录数量。</summary>
    public long EdgeCount => GetRecordCount(EdgePath, EdgeRecordBytes);

    /// <summary>原始根记录数量。</summary>
    public long RootCount => GetRecordCount(RootPath, RootRecordBytes);

    /// <summary>
    /// 从兼容的全量原始捕获创建磁盘 spool；该入口仅服务于旧调用方和测试，新的捕获会直接写入空 spool。
    /// </summary>
    /// <param name="workingDirectory">允许创建一次性子目录的受管目录。</param>
    /// <param name="capture">已冻结的兼容原始捕获。</param>
    /// <param name="cancellationToken">取消文件写入的令牌。</param>
    /// <returns>拥有原始记录目录的 spool。</returns>
    public static async Task<RetentionProfilerRawCaptureSpool> CreateAsync(
        string workingDirectory,
        RetentionProfilerRawCapture capture,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(capture);
        var spool = await CreateEmptyAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        try
        {
            spool.SetEvidence(capture.Functions, capture.Types);
            await WriteObjectsAsync(spool.ObjectPath, capture.Objects, cancellationToken).ConfigureAwait(false);
            await WriteEdgesAsync(spool.EdgePath, capture.Edges, cancellationToken).ConfigureAwait(false);
            await WriteRootsAsync(spool.RootPath, capture.Roots, cancellationToken).ConfigureAwait(false);
            return spool;
        }
        catch
        {
            spool.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 创建三个空的原始记录文件，供共享内存会话按段顺序流式写入。
    /// </summary>
    /// <param name="workingDirectory">允许创建一次性子目录的受管目录。</param>
    /// <param name="cancellationToken">取消目录和文件初始化的令牌。</param>
    /// <returns>可由捕获会话填充的 spool。</returns>
    internal static Task<RetentionProfilerRawCaptureSpool> CreateEmptyAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(workingDirectory);
        var directory = Path.Combine(workingDirectory, $".retention-raw.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var spool = new RetentionProfilerRawCaptureSpool(
            directory,
            Array.Empty<RetentionProfilerRawFunction>(),
            Array.Empty<RetentionProfilerRawType>());
        try
        {
            using (File.Create(spool.ObjectPath)) { }
            using (File.Create(spool.EdgePath)) { }
            using (File.Create(spool.RootPath)) { }
            return Task.FromResult(spool);
        }
        catch
        {
            spool.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 设置从共享内存冻结读取的、受协议容量限制的证据集合。
    /// </summary>
    /// <param name="functions">函数证据。</param>
    /// <param name="types">类型证据。</param>
    internal void SetEvidence(
        IReadOnlyList<RetentionProfilerRawFunction> functions,
        IReadOnlyList<RetentionProfilerRawType> types)
    {
        ThrowIfDisposed();
        Functions = functions ?? throw new ArgumentNullException(nameof(functions));
        Types = types ?? throw new ArgumentNullException(nameof(types));
    }

    /// <summary>
    /// 标记原始 spool 已由最终保留快照和清单完整替代；此后释放可清理一次性原始目录。
    /// </summary>
    internal void DeleteAfterSuccessfulPublication()
    {
        ThrowIfDisposed();
        _deleteOnDispose = true;
    }

    /// <summary>
    /// 以固定宽度小端布局追加一个原始对象记录。
    /// </summary>
    internal static async Task WriteObjectAsync(Stream stream, RetentionProfilerRawObject value, CancellationToken cancellationToken)
    {
        var buffer = new byte[ObjectRecordBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, (ulong)value.ObjectId);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(sizeof(ulong)), (ulong)value.ClassId);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(sizeof(ulong) * 2), (ulong)value.SizeBytes);
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 以固定宽度小端布局追加一个原始引用边记录。
    /// </summary>
    internal static async Task WriteEdgeAsync(Stream stream, RetentionProfilerRawEdge value, CancellationToken cancellationToken)
    {
        var buffer = new byte[EdgeRecordBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, (ulong)value.SourceObjectId);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(sizeof(ulong)), (ulong)value.TargetObjectId);
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 以固定宽度小端布局追加一个原始 GC 根记录。
    /// </summary>
    internal static async Task WriteRootAsync(Stream stream, RetentionProfilerRawRoot value, CancellationToken cancellationToken)
    {
        var buffer = new byte[RootRecordBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, (ulong)value.ObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(sizeof(ulong)), value.RootKind);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(sizeof(ulong) + sizeof(uint)), value.RootFlags);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(sizeof(ulong) + sizeof(uint) * 2), (ulong)value.RootId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(sizeof(ulong) * 2 + sizeof(uint) * 2), value.FunctionEvidenceIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(sizeof(ulong) * 2 + sizeof(uint) * 3), value.Reserved);
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 成功发布后删除一次性目录；否则将目录改名为 evidence 归档并保留原始记录。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (!Directory.Exists(DirectoryPath))
            {
                return;
            }

            if (_deleteOnDispose)
            {
                Directory.Delete(DirectoryPath, recursive: true);
                return;
            }

            var parent = Path.GetDirectoryName(DirectoryPath)
                ?? throw new InvalidOperationException("Profiler 原始记录目录缺少父目录。");
            var evidenceDirectory = Path.Combine(parent, $".retention-raw-evidence.{Guid.NewGuid():N}");
            Directory.Move(DirectoryPath, evidenceDirectory);
            DirectoryPath = evidenceDirectory;
        }
        catch (IOException)
        {
            // 捕获失败清理不能覆盖原始诊断错误；下次临时目录回收会继续处理。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上，权限变化不应把成功或失败的捕获语义改写为清理失败。
        }
    }

    /// <summary>
    /// 验证记录文件总是完整的固定宽度序列，再返回记录数。
    /// </summary>
    private static long GetRecordCount(string path, int recordBytes)
    {
        var length = new FileInfo(path).Length;
        if (length % recordBytes != 0)
        {
            throw new InvalidDataException("Profiler 原始记录 spool 长度无效。");
        }

        return length / recordBytes;
    }

    /// <summary>
    /// 将兼容捕获的对象列表写为固定宽度流，不保留额外副本。
    /// </summary>
    private static async Task WriteObjectsAsync(
        string path,
        IReadOnlyList<RetentionProfilerRawObject> values,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteObjectAsync(stream, value, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 将兼容捕获的边列表写为固定宽度流，不保留额外副本。
    /// </summary>
    private static async Task WriteEdgesAsync(
        string path,
        IReadOnlyList<RetentionProfilerRawEdge> values,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteEdgeAsync(stream, value, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 将兼容捕获的根列表写为固定宽度流，不保留额外副本。
    /// </summary>
    private static async Task WriteRootsAsync(
        string path,
        IReadOnlyList<RetentionProfilerRawRoot> values,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteRootAsync(stream, value, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 阻止已清理临时目录的 spool 再被读取或追加。
    /// </summary>
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
