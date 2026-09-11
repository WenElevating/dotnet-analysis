using DotnetAnalysis.Application.Contracts.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 表示覆盖一次保留分析捕获完整生命周期的容量租约；调用方必须持有到快照发布或原始证据归档完成。
/// </summary>
internal interface IRetentionSnapshotCaptureReservation : IDisposable
{
    /// <summary>
    /// 在持有跨进程租约期间按实际临时快照和原始 spool 占用复核最终提升容量。
    /// </summary>
    /// <param name="temporaryPath">待提升的临时专用快照路径。</param>
    /// <param name="cancellationToken">取消当前目录或卷状态检查的令牌。</param>
    /// <returns>容量仍允许发布时完成的任务。</returns>
    Task EnsureCanStoreAsync(string temporaryPath, CancellationToken cancellationToken);
}

/// <summary>
/// 定义保留分析快照在附加 Profiler 前及提升前需要满足的独立存储保护边界。
/// </summary>
internal interface IRetentionSnapshotStorageGuard
{
    /// <summary>
    /// 验证可以开始一项新的保留分析捕获，但绝不为腾出空间删除已有证据。
    /// </summary>
    /// <param name="cancellationToken">取消当前目录或卷状态检查的令牌。</param>
    /// <returns>容量仍允许启动捕获时完成的任务。</returns>
    Task EnsureCanStartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 验证已生成的临时保留快照仍可提升到应用受管目录。
    /// </summary>
    /// <param name="temporaryPath">待提升的临时专用快照路径。</param>
    /// <param name="cancellationToken">取消当前目录或卷状态检查的令牌。</param>
    /// <returns>容量仍允许保留该文件时完成的任务。</returns>
    Task EnsureCanStoreAsync(string temporaryPath, CancellationToken cancellationToken);

    /// <summary>
    /// 在创建原始 spool 前获取覆盖捕获、转换、发布或失败证据归档的跨进程容量租约。
    /// </summary>
    /// <param name="cancellationToken">取消锁等待或初始容量检查的令牌。</param>
    /// <returns>必须由调用方持有到证据生命周期落定并释放的捕获租约。</returns>
    Task<IRetentionSnapshotCaptureReservation> ReserveCaptureAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 获取覆盖容量复核和最终文件提升的独占保留，释放返回的资源后才允许同一受管目录的下一次提升。
    /// </summary>
    /// <param name="temporaryPath">待提升的临时专用快照路径。</param>
    /// <param name="cancellationToken">取消当前等待或容量检查的令牌。</param>
    /// <returns>必须由调用方释放的提升保留。</returns>
    Task<IDisposable> ReservePromotionAsync(string temporaryPath, CancellationToken cancellationToken);
}

/// <summary>
/// 对保留分析快照执行单文件、总保留量和卷空闲空间限制的默认实现。
/// </summary>
/// <remarks>
/// 保护仅拒绝新捕获或新提升，绝不会自动删除已有的函数证据文件。卷空闲空间委托可在测试中替换，
/// 生产默认从快照根目录所属卷读取实时可用字节数。
/// </remarks>
internal sealed class RetentionSnapshotStorageGuard : IRetentionSnapshotStorageGuard
{
    private const string PromotionLockFileName = ".retention-promotion.lock";
    private const long RetentionSnapshotHeaderBytes = 64;
    private const long MaximumSnapshotPayloadBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumProfilerObjectSortWorkingBytes = RetentionProfilerRawCaptureSpool.MaximumObjectBytes * 2;

    /// <summary>
    /// 跨进程捕获或提升锁的最长排队时间；超时后拒绝本次准入，避免无限挂起调用方。
    /// </summary>
    private static readonly TimeSpan s_lockAcquisitionTimeout = TimeSpan.FromSeconds(1);
    /// <summary>
    /// 单个保留分析快照的最大文件大小。
    /// </summary>
    internal const long MaximumSnapshotBytes = MaximumSnapshotPayloadBytes + RetentionSnapshotHeaderBytes;

    /// <summary>
    /// 应用目录内所有已提升保留分析快照的最大总大小。
    /// </summary>
    internal const long MaximumTotalRetentionBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>
    /// 每个卷必须为系统和目标进程保留的最小空闲空间。
    /// </summary>
    internal const long MinimumFreeVolumeBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// 一次捕获从启动到转换完成可能新增的最大并存字节数。
    /// </summary>
    /// <remarks>
    /// 对象、边和根按格式计数上限分别产生 2.4 GB、8 GB 和 3.2 GB raw spool，共 13.6 GB。
    /// 对象外排归并结束前，输入之外还会同时保留等长块文件和等长排序输出，增加 4.8 GB；
    /// 后续“排序对象 + 最大临时 retentionheap”为 4,547,483,712 字节，小于此外排峰值，
    /// 因此完整生命周期的保守新增峰值为 18.4 GB。
    /// </remarks>
    internal const long MaximumCapturePeakAdditionalBytes =
        RetentionProfilerRawCaptureSpool.MaximumSupportedBytes + MaximumProfilerObjectSortWorkingBytes;

    private readonly SnapshotStorageLayout _layout;
    private readonly Func<string, long> _availableFreeSpace;

    /// <summary>
    /// 创建使用指定受管目录和可选卷可用空间来源的存储保护器。
    /// </summary>
    /// <param name="layout">受管快照根目录布局。</param>
    /// <param name="availableFreeSpace">可选卷可用字节数查询；未提供时读取实际卷状态。</param>
    public RetentionSnapshotStorageGuard(
        SnapshotStorageLayout layout,
        Func<string, long>? availableFreeSpace = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _availableFreeSpace = availableFreeSpace ?? GetAvailableFreeSpace;
    }

    /// <inheritdoc />
    public Task EnsureCanStartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var retainedBytes = GetRetainedBytes();
        var availableBytes = _availableFreeSpace(_layout.RootDirectory);
        if (retainedBytes > MaximumTotalRetentionBytes - MaximumSnapshotBytes
            || availableBytes < MinimumFreeVolumeBytes
            || MaximumCapturePeakAdditionalBytes > availableBytes - MinimumFreeVolumeBytes)
        {
            throw CreateLimitException();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task EnsureCanStoreAsync(string temporaryPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        cancellationToken.ThrowIfCancellationRequested();
        var pendingLength = new FileInfo(temporaryPath).Length;
        if (pendingLength < 0 || pendingLength > MaximumSnapshotBytes)
        {
            throw CreateLimitException();
        }

        await EnsureMinimumFreeSpaceAsync(cancellationToken).ConfigureAwait(false);
        var retainedBytes = GetRetainedBytes(Path.GetFullPath(temporaryPath));
        if (pendingLength > MaximumTotalRetentionBytes - retainedBytes)
        {
            throw CreateLimitException();
        }
    }

    /// <inheritdoc />
    public async Task<IRetentionSnapshotCaptureReservation> ReserveCaptureAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_layout.RootDirectory);
        var lockPath = Path.Combine(_layout.RootDirectory, PromotionLockFileName);
        var lockStream = await AcquirePromotionLockAsync(lockPath, cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCanStartAsync(cancellationToken).ConfigureAwait(false);
            return new CaptureReservation(this, lockStream);
        }
        catch
        {
            await lockStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 在捕获已开始且实际文件已经计入卷可用空间后，只复核不得突破的 2 GiB 底线。
    /// </summary>
    /// <param name="cancellationToken">取消当前卷状态检查的令牌。</param>
    /// <returns>卷仍保有最低空闲空间时完成的任务。</returns>
    private Task EnsureMinimumFreeSpaceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_availableFreeSpace(_layout.RootDirectory) < MinimumFreeVolumeBytes)
        {
            throw CreateLimitException();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<IDisposable> ReservePromotionAsync(string temporaryPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        var reservation = await ReserveCaptureAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await reservation.EnsureCanStoreAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            return reservation;
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 以 FileShare.None 打开受管根目录中的固定锁文件；该文件锁跨进程生效，并在取消时以短退避重试。
    /// </summary>
    private static async Task<FileStream> AcquirePromotionLockAsync(string lockPath, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(s_lockAcquisitionTimeout);
        try
        {
            while (true)
            {
                timeoutSource.Token.ThrowIfCancellationRequested();
                try
                {
                    return new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 1,
                        options: FileOptions.Asynchronous | FileOptions.WriteThrough);
                }
                catch (IOException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), timeoutSource.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.SnapshotStorageLimitReached,
                "等待保留分析存储租约超时。");
        }
    }

    /// <summary>
    /// 汇总已提升的专用快照、当前原始 spool 与失败捕获证据；所有证据都计入同一保留分析总配额。
    /// </summary>
    private long GetRetainedBytes(string? excludedPath = null)
    {
        long total = 0;
        foreach (var path in Directory.EnumerateFiles(_layout.RootDirectory, "*.retentionheap", SearchOption.AllDirectories))
        {
            total = AddRetainedFileLength(total, path, excludedPath);
            if (total > MaximumTotalRetentionBytes)
            {
                return total;
            }
        }

        foreach (var rawDirectory in Directory.EnumerateDirectories(
                     _layout.RootDirectory,
                     ".retention-raw*",
                     SearchOption.AllDirectories))
        {
            foreach (var path in Directory.EnumerateFiles(rawDirectory, "*", SearchOption.AllDirectories))
            {
                total = AddRetainedFileLength(total, path, excludedPath);
                if (total > MaximumTotalRetentionBytes)
                {
                    return total;
                }
            }
        }

        return total;
    }

    /// <summary>
    /// 把单个证据文件按饱和语义计入总量；待提升输入可排除，防止同时按已占用和待新增重复计算。
    /// </summary>
    /// <param name="currentBytes">此前已统计的证据字节数。</param>
    /// <param name="path">当前证据文件路径。</param>
    /// <param name="excludedPath">可选的待提升文件绝对路径。</param>
    /// <returns>累计值；超过配额时稳定返回配额加一。</returns>
    private static long AddRetainedFileLength(long currentBytes, string path, string? excludedPath)
    {
        if (excludedPath is not null
            && string.Equals(Path.GetFullPath(path), excludedPath, StringComparison.OrdinalIgnoreCase))
        {
            return currentBytes;
        }

        var length = new FileInfo(path).Length;
        return length > MaximumTotalRetentionBytes - currentBytes
            ? MaximumTotalRetentionBytes + 1
            : currentBytes + length;
    }

    /// <summary>
    /// 获取指定路径所在卷的当前可用空间。
    /// </summary>
    private static long GetAvailableFreeSpace(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new IOException("无法确定保留分析快照目录所属卷。");
        }

        return new DriveInfo(root).AvailableFreeSpace;
    }

    /// <summary>
    /// 创建稳定且不泄漏卷路径的容量不足错误。
    /// </summary>
    private static DiagnosticsException CreateLimitException() => new(
        DiagnosticsErrorCode.SnapshotStorageLimitReached,
        "保留分析快照超过单文件、总保留量或卷剩余空间限制。");

    /// <summary>
    /// 释放一次跨进程文件锁；重复释放不会让锁状态错误变化。
    /// </summary>
    private sealed class CaptureReservation : IRetentionSnapshotCaptureReservation
    {
        private readonly RetentionSnapshotStorageGuard _owner;
        private FileStream? _lockStream;

        /// <summary>
        /// 创建绑定到指定目录锁文件和容量保护器的捕获保留。
        /// </summary>
        /// <param name="owner">执行持锁容量复核的保护器。</param>
        /// <param name="lockStream">持有跨进程独占锁的文件流。</param>
        public CaptureReservation(RetentionSnapshotStorageGuard owner, FileStream lockStream)
        {
            _owner = owner;
            _lockStream = lockStream;
        }

        /// <inheritdoc />
        public Task EnsureCanStoreAsync(string temporaryPath, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_lockStream is null, this);
            return _owner.EnsureCanStoreAsync(temporaryPath, cancellationToken);
        }

        /// <inheritdoc />
        public void Dispose() => Interlocked.Exchange(ref _lockStream, null)?.Dispose();
    }
}
