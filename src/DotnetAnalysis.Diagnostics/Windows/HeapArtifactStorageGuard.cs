using DotnetAnalysis.Application.Contracts.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 表示一次进程级堆工件容量预留，并负责在全部输出落盘后原子协调实际占用与其他活动预留。
/// </summary>
internal interface IHeapArtifactPublicationReservation : IDisposable
{
    /// <summary>
    /// 在原子发布前按实际工件大小复核容量，并把当前租约从预估峰值协调为已落盘状态。
    /// </summary>
    /// <param name="temporaryDirectory">包含最终清单的完整临时发布目录。</param>
    /// <param name="cancellationToken">取消实际目录扫描。</param>
    /// <returns>实际占用及其他活动预留仍满足容量边界时完成的任务。</returns>
    Task ReconcileActualUsageAsync(string temporaryDirectory, CancellationToken cancellationToken);
}

/// <summary>
/// 保护单个快照的原始文件、基础索引、派生分析和构建工作区构成的工件族配额；只拒绝新的发布，绝不删除已有证据。
/// </summary>
internal sealed class HeapArtifactStorageGuard
{
    private static readonly object s_reservationGate = new();
    private static readonly List<ActiveReservation> s_activeReservations = [];
    private static long s_nextReservationId;

    private const long IndexBuildFixedOverheadBytes = 16L * 1024 * 1024;
    private const long IndexBuildBytesPerObject = 128;
    private const long IndexBuildBytesPerEdge = 96;
    private const long SourceMetadataExpansionFactor = 8;
    private const long DerivedBuildFixedOverheadBytes = 1024 * 1024;
    private const long DerivedBuildBytesPerObject = 96;

    /// <summary>
    /// 单个快照工件族允许占用的最大字节数，包含原始快照、已发布及临时索引文件。
    /// </summary>
    internal const long MaximumArtifactFamilyBytes = 20L * 1024 * 1024 * 1024;

    /// <summary>
    /// 索引构建不得侵占的卷剩余空间，保护操作系统、诊断程序和被诊断目标。
    /// </summary>
    internal const long MinimumFreeVolumeBytes = 2L * 1024 * 1024 * 1024;

    private readonly SnapshotStorageLayout _layout;
    private readonly Func<string, long> _availableFreeSpace;

    /// <summary>
    /// 创建一个使用指定快照布局和可替换卷空间来源的工件族保护器。
    /// </summary>
    /// <param name="layout">受管快照目录布局。</param>
    /// <param name="availableFreeSpace">可选的卷可用空间查询，测试可提供确定值。</param>
    public HeapArtifactStorageGuard(SnapshotStorageLayout layout, Func<string, long>? availableFreeSpace = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _availableFreeSpace = availableFreeSpace ?? GetAvailableFreeSpace;
    }

    /// <summary>
    /// 从即将发布的工件路径推导受管快照根目录，供基础与派生工件发布器使用。
    /// </summary>
    /// <param name="targetDirectory">.heapidx 或 .heapderived 最终目录。</param>
    /// <returns>使用真实卷空间来源的保护器。</returns>
    internal static HeapArtifactStorageGuard CreateForTargetDirectory(string targetDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        var fullTarget = Path.GetFullPath(targetDirectory);
        var snapshotDirectory = Path.GetFileName(fullTarget).Equals(".heapderived", StringComparison.Ordinal)
            ? Path.GetDirectoryName(Path.GetDirectoryName(fullTarget) ?? string.Empty)
            : Path.GetFileName(fullTarget).Equals(".heapidx", StringComparison.Ordinal)
                ? Path.GetDirectoryName(fullTarget)
                : null;
        var rootDirectory = snapshotDirectory is null ? null : Path.GetDirectoryName(snapshotDirectory);
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new InvalidDataException("无法从堆索引工件路径推导快照根目录。");
        }

        return new HeapArtifactStorageGuard(new SnapshotStorageLayout(rootDirectory));
    }

    /// <summary>
    /// 在发布前或临时工件写完后校验空间边界；临时目录计入同一快照工件族，避免完成时才发现配额越界。
    /// </summary>
    /// <param name="targetDirectory">将被原子发布的 .heapidx 或 .heapderived 目录。</param>
    /// <param name="temporaryDirectory">可选的当前构建临时目录。</param>
    /// <param name="cancellationToken">取消当前文件系统扫描。</param>
    /// <returns>容量允许继续发布时完成的任务。</returns>
    /// <exception cref="DiagnosticsException">卷余量或工件族配额不足时引发。</exception>
    public Task EnsureCanPublishAsync(string targetDirectory, string? temporaryDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshotDirectory = GetSnapshotDirectory(targetDirectory);
        var usedBytes = GetDirectorySize(snapshotDirectory, cancellationToken);
        if (temporaryDirectory is not null
            && Directory.Exists(temporaryDirectory)
            && !IsDescendantOf(temporaryDirectory, snapshotDirectory))
        {
            usedBytes = checked(usedBytes + GetDirectorySize(temporaryDirectory, cancellationToken));
        }

        if (usedBytes > MaximumArtifactFamilyBytes
            || _availableFreeSpace(_layout.RootDirectory) < MinimumFreeVolumeBytes)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.SnapshotStorageLimitReached,
                "快照原始文件、索引、派生分析或构建工作区超过工件族配额或卷剩余空间限制。");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 在创建构建工作区前按保守峰值估算预留工件族和卷容量，避免已知的大规模输出先耗尽空间再被拒绝。
    /// </summary>
    /// <param name="targetDirectory">将被原子发布的 .heapidx 或 .heapderived 目录。</param>
    /// <param name="estimatedPeakAdditionalBytes">从当前状态到构建峰值最多新增的字节数。</param>
    /// <param name="cancellationToken">取消当前文件系统扫描。</param>
    /// <returns>容量足以覆盖估算峰值时完成的任务。</returns>
    /// <exception cref="ArgumentOutOfRangeException">估算增长为负数时引发。</exception>
    /// <exception cref="DiagnosticsException">工件族或卷容量不能覆盖估算峰值时引发。</exception>
    public Task EnsureCanStartPublicationAsync(
        string targetDirectory,
        long estimatedPeakAdditionalBytes,
        CancellationToken cancellationToken)
    {
        using var reservation = ReservePublication(
            targetDirectory,
            estimatedPeakAdditionalBytes,
            cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 为一次完整工件发布登记进程级容量租约；同卷和同快照族的后续发布必须计入尚未释放的峰值。
    /// </summary>
    /// <param name="targetDirectory">将被原子发布的 .heapidx 或 .heapderived 目录。</param>
    /// <param name="estimatedPeakAdditionalBytes">从当前状态到构建峰值最多新增的字节数，包含最终清单。</param>
    /// <param name="cancellationToken">取消获取租约前的容量扫描。</param>
    /// <returns>覆盖整个发布生命周期且必须释放的租约。</returns>
    /// <exception cref="ArgumentOutOfRangeException">估算增长为负数时引发。</exception>
    /// <exception cref="DiagnosticsException">工件族或卷容量已被占用时引发。</exception>
    public Task<IHeapArtifactPublicationReservation> ReservePublicationAsync(
        string targetDirectory,
        long estimatedPeakAdditionalBytes,
        CancellationToken cancellationToken) =>
        Task.FromResult<IHeapArtifactPublicationReservation>(
            ReservePublication(targetDirectory, estimatedPeakAdditionalBytes, cancellationToken));

    /// <summary>
    /// 在进程级临界区中复核实际文件和现有租约，再原子登记本次峰值。
    /// </summary>
    private PublicationReservation ReservePublication(
        string targetDirectory,
        long estimatedPeakAdditionalBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedPeakAdditionalBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshotDirectory = GetSnapshotDirectory(targetDirectory);
        var volumeKey = GetVolumeKey(_layout.RootDirectory);
        var familyKey = Path.TrimEndingDirectorySeparator(Path.GetFullPath(snapshotDirectory));
        lock (s_reservationGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var usedBytes = GetDirectorySize(snapshotDirectory, cancellationToken);
            var availableBytes = _availableFreeSpace(_layout.RootDirectory);
            var volumeReservedBytes = SumReservedBytes(static (reservation, key) =>
                string.Equals(reservation.VolumeKey, key, StringComparison.OrdinalIgnoreCase), volumeKey);
            var familyReservedBytes = SumReservedBytes(static (reservation, key) =>
                string.Equals(reservation.FamilyKey, key, StringComparison.OrdinalIgnoreCase), familyKey);
            if (!CanFit(usedBytes, familyReservedBytes, MaximumArtifactFamilyBytes, estimatedPeakAdditionalBytes)
                || !CanFit(MinimumFreeVolumeBytes, volumeReservedBytes, availableBytes, estimatedPeakAdditionalBytes))
            {
                throw CreateLimitException();
            }

            var reservationId = ++s_nextReservationId;
            s_activeReservations.Add(new ActiveReservation(
                reservationId,
                volumeKey,
                familyKey,
                estimatedPeakAdditionalBytes));
            return new PublicationReservation(this, reservationId, targetDirectory);
        }
    }

    /// <summary>
    /// 在进程级租约锁内用实际文件占用替换当前预估，同时保留其他活动发布的全部容量承诺。
    /// </summary>
    private Task ReconcileActualUsageAsync(
        long reservationId,
        string targetDirectory,
        string temporaryDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshotDirectory = GetSnapshotDirectory(targetDirectory);
        lock (s_reservationGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reservation = s_activeReservations.SingleOrDefault(item => item.Id == reservationId)
                ?? throw new ObjectDisposedException(nameof(PublicationReservation));
            var usedBytes = GetDirectorySize(snapshotDirectory, cancellationToken);
            if (Directory.Exists(temporaryDirectory)
                && !IsDescendantOf(temporaryDirectory, snapshotDirectory))
            {
                usedBytes = checked(usedBytes + GetDirectorySize(temporaryDirectory, cancellationToken));
            }

            var availableBytes = _availableFreeSpace(_layout.RootDirectory);
            var otherVolumeReservedBytes = SumReservedBytes(
                static (item, state) => item.Id != state.Id
                    && string.Equals(item.VolumeKey, state.Key, StringComparison.OrdinalIgnoreCase),
                (Id: reservationId, Key: reservation.VolumeKey));
            var otherFamilyReservedBytes = SumReservedBytes(
                static (item, state) => item.Id != state.Id
                    && string.Equals(item.FamilyKey, state.Key, StringComparison.OrdinalIgnoreCase),
                (Id: reservationId, Key: reservation.FamilyKey));
            if (!CanFit(usedBytes, otherFamilyReservedBytes, MaximumArtifactFamilyBytes, 0)
                || !CanFit(MinimumFreeVolumeBytes, otherVolumeReservedBytes, availableBytes, 0))
            {
                throw CreateLimitException();
            }

            reservation.ReservedBytes = 0;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 根据流式索引已知的对象、边和源元数据规模，计算包含外排中间文件的保守磁盘峰值。
    /// </summary>
    /// <param name="objectCount">快照声明的对象数。</param>
    /// <param name="edgeCount">快照声明或上界估算的引用边数。</param>
    /// <param name="sourceLengthBytes">包含类型及根证据等变长元数据的源文件大小。</param>
    /// <returns>不超过 <see cref="MaximumArtifactFamilyBytes"/> 加一的饱和估算值。</returns>
    internal static long EstimateIndexBuildPeakBytes(long objectCount, long edgeCount, long sourceLengthBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(objectCount);
        ArgumentOutOfRangeException.ThrowIfNegative(edgeCount);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLengthBytes);
        return SaturatingEstimate(
            IndexBuildFixedOverheadBytes,
            (objectCount, IndexBuildBytesPerObject),
            (edgeCount, IndexBuildBytesPerEdge),
            (sourceLengthBytes, SourceMetadataExpansionFactor));
    }

    /// <summary>
    /// 根据基础对象数估算支配树状态、排序结果和清单的最大新增磁盘量。
    /// </summary>
    /// <param name="objectCount">基础索引中的对象数。</param>
    /// <returns>不超过 <see cref="MaximumArtifactFamilyBytes"/> 加一的饱和估算值。</returns>
    internal static long EstimateDerivedBuildPeakBytes(long objectCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(objectCount);
        return SaturatingEstimate(
            DerivedBuildFixedOverheadBytes,
            (objectCount, DerivedBuildBytesPerObject));
    }

    /// <summary>
    /// 对若干计数乘积执行饱和加法；超出族配额时返回配额加一，使调用方稳定走容量错误而非溢出错误。
    /// </summary>
    private static long SaturatingEstimate(long fixedBytes, params (long Count, long BytesPerItem)[] terms)
    {
        var limit = MaximumArtifactFamilyBytes + 1;
        var total = fixedBytes;
        foreach (var (count, bytesPerItem) in terms)
        {
            if (count > (limit - total) / bytesPerItem)
            {
                return limit;
            }

            total += count * bytesPerItem;
        }

        return total;
    }

    /// <summary>
    /// 判断当前占用、既有租约和新峰值是否可共同落在指定容量内，避免减法或加法溢出。
    /// </summary>
    private static bool CanFit(long usedBytes, long reservedBytes, long capacityBytes, long requestedBytes) =>
        usedBytes >= 0
        && reservedBytes >= 0
        && capacityBytes >= usedBytes
        && reservedBytes <= capacityBytes - usedBytes
        && requestedBytes <= capacityBytes - usedBytes - reservedBytes;

    /// <summary>
    /// 对满足谓词的活动租约执行饱和求和，异常大的并发量按不可再分配处理。
    /// </summary>
    private static long SumReservedBytes<TState>(Func<ActiveReservation, TState, bool> predicate, TState state)
    {
        long total = 0;
        foreach (var reservation in s_activeReservations)
        {
            if (!predicate(reservation, state))
            {
                continue;
            }

            if (reservation.ReservedBytes > long.MaxValue - total)
            {
                return long.MaxValue;
            }

            total += reservation.ReservedBytes;
        }

        return total;
    }

    /// <summary>
    /// 由 .heapidx 或 .heapderived 路径回溯其所属快照目录，并拒绝任何不在受管根目录中的路径。
    /// </summary>
    private string GetSnapshotDirectory(string targetDirectory)
    {
        var fullTarget = Path.GetFullPath(targetDirectory);
        var name = Path.GetFileName(fullTarget);
        var snapshotDirectory = name.Equals(".heapderived", StringComparison.Ordinal)
            ? Path.GetDirectoryName(Path.GetDirectoryName(fullTarget) ?? string.Empty)
            : name.Equals(".heapidx", StringComparison.Ordinal)
                ? Path.GetDirectoryName(fullTarget)
                : null;
        if (string.IsNullOrWhiteSpace(snapshotDirectory)
            || !IsDescendantOf(snapshotDirectory, _layout.RootDirectory))
        {
            throw new InvalidDataException("堆索引工件目录不在受管快照根目录内。");
        }

        return snapshotDirectory;
    }

    /// <summary>
    /// 顺序累计目录内文件大小；无法读取的目录或溢出由调用方转换为索引构建失败。
    /// </summary>
    private static long GetDirectorySize(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            total = checked(total + new FileInfo(path).Length);
        }

        return total;
    }

    /// <summary>
    /// 创建不泄漏本地卷路径的稳定容量错误。
    /// </summary>
    private static DiagnosticsException CreateLimitException() => new(
        DiagnosticsErrorCode.SnapshotStorageLimitReached,
        "快照原始文件、索引、派生分析或构建工作区超过工件族配额或卷剩余空间限制。");

    /// <summary>
    /// 获取路径所属卷的规范键，确保同一进程内不同快照根共享卷余量租约。
    /// </summary>
    private static string GetVolumeKey(string directory)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directory));
        return string.IsNullOrWhiteSpace(root)
            ? throw new IOException("无法确定快照工件目录所属卷。")
            : Path.TrimEndingDirectorySeparator(root);
    }

    /// <summary>
    /// 判断 candidate 是否位于 parent 内部或与 parent 相同，避免字符串前缀误判。
    /// </summary>
    private static bool IsDescendantOf(string candidate, string parent)
    {
        var fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var fullParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return string.Equals(fullCandidate, fullParent, StringComparison.OrdinalIgnoreCase)
            || fullCandidate.StartsWith(fullParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 获取指定目录所属卷的实时可用空间。
    /// </summary>
    private static long GetAvailableFreeSpace(string directory)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directory));
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new IOException("无法确定快照工件目录所属卷。");
        }

        return new DriveInfo(root).AvailableFreeSpace;
    }

    /// <summary>
    /// 描述一次尚未完成的进程内工件发布峰值。
    /// </summary>
    private sealed class ActiveReservation
    {
        /// <summary>
        /// 创建一次活动容量承诺；协调完成前 <paramref name="reservedBytes"/> 表示尚需保护的峰值。
        /// </summary>
        public ActiveReservation(long id, string volumeKey, string familyKey, long reservedBytes)
        {
            Id = id;
            VolumeKey = volumeKey;
            FamilyKey = familyKey;
            ReservedBytes = reservedBytes;
        }

        public long Id { get; }

        public string VolumeKey { get; }

        public string FamilyKey { get; }

        public long ReservedBytes { get; set; }
    }

    /// <summary>
    /// 在发布成功或异常退出时从进程级容量账本移除一次租约。
    /// </summary>
    private sealed class PublicationReservation : IHeapArtifactPublicationReservation
    {
        private readonly HeapArtifactStorageGuard _owner;
        private readonly string _targetDirectory;
        private long _reservationId;

        /// <summary>
        /// 创建绑定到活动租约标识的释放句柄。
        /// </summary>
        public PublicationReservation(
            HeapArtifactStorageGuard owner,
            long reservationId,
            string targetDirectory)
        {
            _owner = owner;
            _reservationId = reservationId;
            _targetDirectory = targetDirectory;
        }

        /// <inheritdoc />
        public Task ReconcileActualUsageAsync(string temporaryDirectory, CancellationToken cancellationToken)
        {
            var reservationId = Volatile.Read(ref _reservationId);
            ObjectDisposedException.ThrowIf(reservationId == 0, this);
            return _owner.ReconcileActualUsageAsync(
                reservationId,
                _targetDirectory,
                temporaryDirectory,
                cancellationToken);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            var reservationId = Interlocked.Exchange(ref _reservationId, 0);
            if (reservationId == 0)
            {
                return;
            }

            lock (s_reservationGate)
            {
                _ = s_activeReservations.RemoveAll(reservation => reservation.Id == reservationId);
            }
        }
    }
}
