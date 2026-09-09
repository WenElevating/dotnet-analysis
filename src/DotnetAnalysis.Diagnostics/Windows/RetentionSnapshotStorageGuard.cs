using DotnetAnalysis.Application.Contracts.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

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
    /// <summary>
    /// 单个保留分析快照的最大文件大小。
    /// </summary>
    internal const long MaximumSnapshotBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// 应用目录内所有已提升保留分析快照的最大总大小。
    /// </summary>
    internal const long MaximumTotalRetentionBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>
    /// 每个卷必须为系统和目标进程保留的最小空闲空间。
    /// </summary>
    internal const long MinimumFreeVolumeBytes = 2L * 1024 * 1024 * 1024;

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
        if (retainedBytes >= MaximumTotalRetentionBytes || availableBytes < MinimumFreeVolumeBytes)
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

        await EnsureCanStartAsync(cancellationToken).ConfigureAwait(false);
        var retainedBytes = GetRetainedBytes(Path.GetFullPath(temporaryPath));
        if (pendingLength > MaximumTotalRetentionBytes - retainedBytes)
        {
            throw CreateLimitException();
        }
    }

    /// <inheritdoc />
    public async Task<IDisposable> ReservePromotionAsync(string temporaryPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        Directory.CreateDirectory(_layout.RootDirectory);
        var lockPath = Path.Combine(_layout.RootDirectory, PromotionLockFileName);
        var lockStream = await AcquirePromotionLockAsync(lockPath, cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCanStoreAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            return new PromotionReservation(lockStream);
        }
        catch
        {
            await lockStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 以 FileShare.None 打开受管根目录中的固定锁文件；该文件锁跨进程生效，并在取消时以短退避重试。
    /// </summary>
    private static async Task<FileStream> AcquirePromotionLockAsync(string lockPath, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 汇总已提升的专用快照文件大小；临时文件不计入当前证据总量。
    /// </summary>
    private long GetRetainedBytes(string? excludedPath = null)
    {
        long total = 0;
        foreach (var path in Directory.EnumerateFiles(_layout.RootDirectory, "*.retentionheap", SearchOption.AllDirectories))
        {
            if (excludedPath is not null
                && string.Equals(Path.GetFullPath(path), excludedPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            checked
            {
                total += new FileInfo(path).Length;
            }

            if (total > MaximumTotalRetentionBytes)
            {
                return total;
            }
        }

        return total;
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
    private sealed class PromotionReservation : IDisposable
    {
        private FileStream? _lockStream;

        /// <summary>
        /// 创建绑定到指定目录锁文件的提升保留。
        /// </summary>
        public PromotionReservation(FileStream lockStream) => _lockStream = lockStream;

        /// <inheritdoc />
        public void Dispose() => Interlocked.Exchange(ref _lockStream, null)?.Dispose();
    }
}
