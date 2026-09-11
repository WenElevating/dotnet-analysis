using System.Security.Cryptography;
using System.Text.Json;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 根据对象数、图估算大小和可用内存，在常驻索引与不可变磁盘索引之间自动路由。
/// </summary>
internal sealed class HeapIndexRouter
{
    private readonly SnapshotStorageLayout _layout;
    private readonly long _maximumInMemoryObjectCount;
    private readonly long _maximumInMemoryBytes;

    /// <summary>
    /// 创建自动索引路由器。
    /// </summary>
    /// <param name="layout">快照工件目录布局。</param>
    /// <param name="maximumInMemoryObjectCount">测试或资源策略指定的内存索引对象上限。</param>
    /// <param name="maximumInMemoryBytes">测试或资源策略指定的内存索引估算字节上限。</param>
    public HeapIndexRouter(
        SnapshotStorageLayout layout,
        long? maximumInMemoryObjectCount = null,
        long? maximumInMemoryBytes = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _maximumInMemoryObjectCount = maximumInMemoryObjectCount ?? MemorySnapshotAnalysis.FullObjectEnumerationLimit;
        _maximumInMemoryBytes = maximumInMemoryBytes ?? HeapIndexResourcePolicy.GetBudgetBytes();
        ArgumentOutOfRangeException.ThrowIfNegative(_maximumInMemoryObjectCount);
        ArgumentOutOfRangeException.ThrowIfNegative(_maximumInMemoryBytes);
    }

    /// <summary>
    /// 返回可查询索引句柄；调用方无需选择小快照或大快照实现。
    /// </summary>
    public async Task<HeapIndexHandle> RouteAsync(
        MemorySnapshotId snapshotId,
        SnapshotIndex index,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(index);
        cancellationToken.ThrowIfCancellationRequested();
        var data = index.ExportArtifactData();
        var edgeCount = data.Edges.Sum(pair => (long)pair.Value.Count);
        if (!ShouldUseMapped(data.Objects.Count, edgeCount))
        {
            return HeapIndexHandle.CreateInMemory(index);
        }

        var directory = _layout.GetHeapIndexDirectory(snapshotId);
        await HeapIndexArtifactStore.PublishAsync(directory, data, cancellationToken).ConfigureAwait(false);
        return HeapIndexHandle.CreateMapped(directory);
    }

    /// <summary>
    /// 根据对象、边与自适应预算判断是否必须避免常驻整个对象图；达到公开分页阈值时始终选择磁盘索引。
    /// </summary>
    internal bool ShouldUseMapped(long objectCount, long edgeCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(objectCount);
        ArgumentOutOfRangeException.ThrowIfNegative(edgeCount);
        var estimatedBytes = checked(objectCount * 48L + edgeCount * 16L);
        return objectCount >= _maximumInMemoryObjectCount || estimatedBytes > _maximumInMemoryBytes;
    }

    /// <summary>
    /// 在路由器拥有的快照目录中发布读取器直接写出的磁盘工件，发布成功后返回不持有整图的映射句柄。
    /// </summary>
    internal async Task<HeapIndexHandle> BuildMappedAsync(
        MemorySnapshotId snapshotId,
        Func<string, CancellationToken, Task> publishAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publishAsync);
        var directory = _layout.GetHeapIndexDirectory(snapshotId);
        await publishAsync(directory, cancellationToken).ConfigureAwait(false);
        return HeapIndexHandle.CreateMapped(directory);
    }

    /// <summary>
    /// 返回指定快照的基础索引目标目录，供格式读取器在同一工件族内创建受控构建工作区。
    /// </summary>
    /// <param name="snapshotId">快照稳定标识。</param>
    /// <returns>该快照的最终 <c>.heapidx</c> 目录。</returns>
    internal string GetHeapIndexDirectory(MemorySnapshotId snapshotId) => _layout.GetHeapIndexDirectory(snapshotId);

    /// <summary>
    /// 在读取原始快照或申请构建资源前尝试打开已完整校验的 v3 工件；损坏或不完整目录返回空并由后续发布归档重建。
    /// </summary>
    /// <param name="snapshotId">待复用工件所属快照。</param>
    /// <param name="cancellationToken">取消 manifest、长度与摘要校验。</param>
    /// <returns>完整工件对应的映射句柄；不存在或校验失败时返回空。</returns>
    internal async Task<HeapIndexHandle?> TryOpenExistingMappedAsync(
        MemorySnapshotId snapshotId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = _layout.GetHeapIndexDirectory(snapshotId);
        return await HeapIndexArtifactStore.IsCompleteAsync(directory, cancellationToken).ConfigureAwait(false)
            ? HeapIndexHandle.CreateMapped(directory)
            : null;
    }

}

/// <summary>
/// 封装当前快照的自动路由索引；映射版本持有工件映射资源，在快照切换时由缓存释放。
/// </summary>
internal sealed class HeapIndexHandle : IDisposable
{
    private readonly SharedState _state;
    private int _disposed;

    /// <summary>
    /// 创建共享索引资源上的一个所有权包装；调用方必须独立释放当前包装。
    /// </summary>
    private HeapIndexHandle(SharedState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    /// <summary>
    /// 基础查询索引；该属性只在 Diagnostics 内部使用。
    /// </summary>
    public bool IsInMemoryGraphResident => State.InMemoryIndex is not null;

    /// <summary>
    /// 是否已发布并打开磁盘常驻索引工件。
    /// </summary>
    public bool IsMapped => State.MappedIndex is not null;

    /// <summary>
    /// 类型统计代理。
    /// </summary>
    public IReadOnlyList<MemoryTypeSummary> TypeSummaries => State.InMemoryIndex?.TypeSummaries ?? State.MappedIndex!.TypeSummaries;

    /// <summary>
    /// 当前快照对象读取方式；大型快照只允许分页查询。
    /// </summary>
    public MemorySnapshotObjectAccessMode ObjectAccessMode => State.InMemoryIndex?.ObjectAccessMode ?? State.MappedIndex!.ObjectAccessMode;

    /// <summary>
    /// 获取指定类型的完整对象列表；大型快照会稳定拒绝并要求调用方改用分页。
    /// </summary>
    public IReadOnlyList<MemoryObjectInfo> GetObjects(TypeIdentity type)
    {
        var state = State;
        return state.InMemoryIndex?.GetObjects(type) ?? state.MappedIndex!.GetObjects(type);
    }

    /// <summary>
    /// 获取对象分页，调用方不需要知道索引存储介质。
    /// </summary>
    public MemoryObjectPage GetPage(TypeIdentity type, int offset, int pageSize)
    {
        var state = State;
        return state.InMemoryIndex?.GetPage(type, offset, pageSize) ?? state.MappedIndex!.GetPage(type, offset, pageSize);
    }

    /// <summary>
    /// 获取目标对象的一条 GC 根引用路径；不存在已验证根链时返回空，取消只作用于当前遍历。
    /// </summary>
    public MemoryReferencePath? GetReferencePath(ulong address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = State;
        if (state.InMemoryIndex is not null)
        {
            var paths = state.InMemoryIndex.GetRetentionPaths(address, 1, cancellationToken);
            return paths is null ? null : new MemoryReferencePath(address, paths.Paths[0].Objects);
        }

        return state.MappedIndex!.GetReferencePath(address, cancellationToken);
    }

    /// <summary>
    /// 获取目标对象的保留路径；调用方取消仅终止本次遍历。
    /// </summary>
    public MemoryRetentionPathResult? GetRetentionPaths(ulong address, int maximum, CancellationToken cancellationToken)
    {
        var state = State;
        return state.InMemoryIndex?.GetRetentionPaths(address, maximum, cancellationToken)
            ?? state.MappedIndex!.GetRetentionPaths(address, maximum, cancellationToken);
    }

    /// <summary>
    /// 异步获取支配树分页；映射索引会共享后台派生任务，单次调用取消仅取消等待。
    /// </summary>
    /// <param name="offset">零基结果偏移量。</param>
    /// <param name="size">每页对象数，范围为 1 至 1000。</param>
    /// <param name="cancellationToken">取消当前等待或分页投影的令牌。</param>
    /// <returns>按 retained size 排序的支配对象页。</returns>
    public Task<MemoryDominatorPage> GetDominatorPageAsync(int offset, int size, CancellationToken cancellationToken)
    {
        var state = State;
        return state.InMemoryIndex is not null
            ? Task.Run(() => state.InMemoryIndex.GetDominatorPage(offset, size, cancellationToken), CancellationToken.None)
            : state.MappedIndex!.GetDominatorPageAsync(offset, size, cancellationToken);
    }

    /// <summary>
    /// 创建常驻内存索引句柄。
    /// </summary>
    public static HeapIndexHandle CreateInMemory(SnapshotIndex index) =>
        new(new SharedState(index ?? throw new ArgumentNullException(nameof(index))));

    /// <summary>
    /// 创建并验证已原子发布的磁盘索引句柄；实际映射窗口应仅在按需查询期间短暂打开，不能阻碍快照目录清理。
    /// </summary>
    public static HeapIndexHandle CreateMapped(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var objectPath = Path.Combine(directory, "objects.bin");
        if (!File.Exists(objectPath))
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.SnapshotIndexBuildFailed, "已发布堆索引缺少对象工件。");
        }

        return new HeapIndexHandle(new SharedState(new MappedHeapIndex(directory)));
    }

    /// <summary>
    /// 为调用方取得一份独立租约；底层映射资源由缓存所有权和所有活动租约共同持有。
    /// </summary>
    /// <returns>必须由当前调用方释放的共享索引租约。</returns>
    internal HeapIndexHandle AcquireLease()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _state.AddReference();
        return new HeapIndexHandle(_state);
    }

    /// <summary>
    /// 释放当前包装的一份所有权；只有最后一份所有权会取消并释放底层映射索引。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _state.Release();
        }
    }

    /// <summary>
    /// 返回当前包装仍有权访问的共享状态，防止已释放租约继续发起查询。
    /// </summary>
    private SharedState State
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _state;
        }
    }

    /// <summary>
    /// 保存单个实际索引及其引用计数；引用从一开始代表创建者或缓存的所有权。
    /// </summary>
    private sealed class SharedState
    {
        private int _referenceCount = 1;

        /// <summary>
        /// 创建常驻内存索引共享状态。
        /// </summary>
        public SharedState(SnapshotIndex inMemoryIndex)
        {
            InMemoryIndex = inMemoryIndex;
        }

        /// <summary>
        /// 创建映射索引共享状态。
        /// </summary>
        public SharedState(MappedHeapIndex mappedIndex)
        {
            MappedIndex = mappedIndex;
        }

        /// <summary>
        /// 可选常驻内存索引。
        /// </summary>
        public SnapshotIndex? InMemoryIndex { get; }

        /// <summary>
        /// 可选映射索引；其取消源只在最后一个引用离开时释放。
        /// </summary>
        public MappedHeapIndex? MappedIndex { get; }

        /// <summary>
        /// 原子增加调用方租约引用，并拒绝复活已经释放的资源。
        /// </summary>
        public void AddReference()
        {
            while (true)
            {
                var current = Volatile.Read(ref _referenceCount);
                ObjectDisposedException.ThrowIf(current == 0, this);
                if (Interlocked.CompareExchange(ref _referenceCount, current + 1, current) == current)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// 释放一份所有权；最后一份引用负责取消后台派生构建并释放映射状态。
        /// </summary>
        public void Release()
        {
            var remaining = Interlocked.Decrement(ref _referenceCount);
            if (remaining == 0)
            {
                MappedIndex?.Dispose();
            }
            else if (remaining < 0)
            {
                throw new InvalidOperationException("堆索引共享引用计数已失衡。");
            }
        }
    }
}

/// <summary>
/// 以临时目录、SHA-256 校验和原子目录提升方式发布基础堆索引工件。
/// </summary>
internal static class HeapIndexArtifactStore
{
    private const int FormatVersion = 3;

    /// <summary>
    /// v3 基础索引必须同时具备的不可变工件；清单不得通过省略文件来把损坏目录伪装成可复用目录。
    /// </summary>
    private static readonly string[] s_requiredArtifactNames =
    [
        "types.bin",
        "type-summary.bin",
        "objects.bin",
        "address-to-id.bin",
        "objects-by-type.bin",
        "forward-offsets.bin",
        "forward-targets.bin",
        "reverse-offsets.bin",
        "reverse-targets.bin",
        "roots-by-object.bin",
        "root-evidence.bin"
    ];

    /// <summary>
    /// 发布一个完整可验证的 v3 索引目录；已有完成工件会被复用，失败不会触碰原始快照。
    /// </summary>
    public static Task PublishAsync(
        string directory,
        SnapshotIndex.SnapshotIndexArtifactData data,
        CancellationToken cancellationToken) =>
        PublishCoreAsync(
            directory,
            (temporaryDirectory, token) => WriteArtifactsAsync(temporaryDirectory, data, token),
            HeapArtifactStorageGuard.EstimateIndexBuildPeakBytes(
                data.Objects.Count,
                data.Edges.Sum(static pair => (long)pair.Value.Count),
                EstimateVariableMetadataBytes(data)),
            cancellationToken);

    /// <summary>
    /// 使用流式格式读取器写入的基础工件发布目录；回调只能写入临时目录，并返回纳入清单的相对文件名。
    /// </summary>
    internal static Task PublishAsync(
        string directory,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> writeArtifactsAsync,
        long estimatedPeakAdditionalBytes,
        CancellationToken cancellationToken) =>
        PublishCoreAsync(directory, writeArtifactsAsync, estimatedPeakAdditionalBytes, cancellationToken);

    /// <summary>
    /// 统一执行完成工件复用、临时构建、摘要清单与原子目录提升，保证不同快照格式遵守同一恢复语义。
    /// </summary>
    private static async Task PublishCoreAsync(
        string directory,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> writeArtifactsAsync,
        long estimatedPeakAdditionalBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(writeArtifactsAsync);
        using var publicationGate = await HeapArtifactPublicationGate
            .EnterAsync(directory, cancellationToken)
            .ConfigureAwait(false);
        if (await IsCompleteAsync(directory, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var parent = Path.GetDirectoryName(directory) ?? throw new InvalidOperationException("堆索引目录缺少父目录。");
        Directory.CreateDirectory(parent);
        var storageGuard = HeapArtifactStorageGuard.CreateForTargetDirectory(directory);
        using var publicationReservation = await storageGuard
            .ReservePublicationAsync(directory, estimatedPeakAdditionalBytes, cancellationToken)
            .ConfigureAwait(false);
        if (Directory.Exists(directory))
        {
            ArchiveIncompleteArtifactDirectory(directory);
        }

        HeapTemporaryArtifactCleanup.TryDeleteAbandonedDirectories(
            parent,
            $".{Path.GetFileName(directory)}.*.tmp",
            HeapTemporaryArtifactCleanup.AbandonedDirectoryMinimumAge);
        var temporaryDirectory = Path.Combine(parent, $".{Path.GetFileName(directory)}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var paths = await writeArtifactsAsync(temporaryDirectory, cancellationToken).ConfigureAwait(false);
            if (!HasRequiredArtifactNameSet(paths))
            {
                throw new InvalidDataException("索引工件列表必须精确包含 v3 所需文件。");
            }

            var files = await CreateManifestFilesAsync(temporaryDirectory, paths, cancellationToken).ConfigureAwait(false);
            var manifest = new HeapIndexManifest(FormatVersion, true, files);
            await WriteJsonAsync(Path.Combine(temporaryDirectory, "manifest.json"), manifest, cancellationToken).ConfigureAwait(false);
            await publicationReservation
                .ReconcileActualUsageAsync(temporaryDirectory, cancellationToken)
                .ConfigureAwait(false);
            Directory.Move(temporaryDirectory, directory);
        }
        catch (DiagnosticsException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or OverflowException
            or JsonException
            or ArgumentException)
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.SnapshotIndexBuildFailed, "无法发布快照堆索引工件。", exception);
        }
        finally
        {
            HeapTemporaryArtifactCleanup.TryDeleteDirectory(temporaryDirectory);
        }
    }

    /// <summary>
    /// 计算内存索引输入中会重复写入类型和根证据工件的变长 UTF-8 元数据规模。
    /// </summary>
    private static long EstimateVariableMetadataBytes(SnapshotIndex.SnapshotIndexArtifactData data)
    {
        long total = 0;
        foreach (var type in data.Objects.Select(static row => row.Type).Distinct())
        {
            total = checked(total + System.Text.Encoding.UTF8.GetByteCount(type.TypeName));
            total = checked(total + System.Text.Encoding.UTF8.GetByteCount(type.AssemblyName ?? string.Empty));
        }

        foreach (var row in data.Roots)
        {
            total = checked(total + System.Text.Encoding.UTF8.GetByteCount(row.Root.FunctionName ?? string.Empty));
            total = checked(total + System.Text.Encoding.UTF8.GetByteCount(row.Root.ModuleName ?? string.Empty));
        }

        return total;
    }

    /// <summary>
    /// 将不完整或无法验证的基础索引移出活动路径；只归档派生工件，不触碰原始快照文件。
    /// </summary>
    private static void ArchiveIncompleteArtifactDirectory(string directory)
    {
        var parent = Path.GetDirectoryName(directory) ?? throw new InvalidOperationException("堆索引目录缺少父目录。");
        var archiveDirectory = Path.Combine(parent, $"{Path.GetFileName(directory)}.incomplete.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}");
        Directory.Move(directory, archiveDirectory);
    }

    /// <summary>
    /// 验证已有 v3 清单完整列出所有必需工件，且每个工件的长度和 SHA-256 均匹配，避免复用中断、截断或被篡改的目录。
    /// </summary>
    internal static async Task<bool> IsCompleteAsync(string directory, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            await using var stream = File.OpenRead(manifestPath);
            var manifest = await JsonSerializer.DeserializeAsync<HeapIndexManifest>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (manifest is null
                || manifest.Version != FormatVersion
                || !manifest.Completed
                || !HasRequiredArtifactSet(manifest.Files))
            {
                return false;
            }

            foreach (var item in manifest.Files)
            {
                var path = Path.Combine(directory, item.Name);
                if (!File.Exists(path) || new FileInfo(path).Length != item.Length)
                {
                    return false;
                }

                var hash = await ComputeHashAsync(path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(Convert.ToHexString(hash), item.Sha256, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            await HeapIndexSemanticValidator.ValidateAsync(directory, cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (Exception exception) when (exception is IOException
            or JsonException
            or UnauthorizedAccessException
            or InvalidDataException
            or EndOfStreamException
            or OverflowException
            or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 验证清单文件名恰好等于 v3 固定工件集合；拒绝遗漏、重复或额外路径，
    /// 使后续映射读取器不必把不完整清单当作可信输入。
    /// </summary>
    /// <param name="files">清单中声明的工件条目。</param>
    /// <returns>清单精确包含所有必需工件时返回 <see langword="true"/>。</returns>
    private static bool HasRequiredArtifactSet(IReadOnlyList<HeapIndexArtifactFile>? files)
    {
        if (files is null || files.Count != s_requiredArtifactNames.Length)
        {
            return false;
        }

        if (files.Any(static file => file is null))
        {
            return false;
        }

        var names = files.Select(static file => file.Name).ToHashSet(StringComparer.Ordinal);
        return names.Count == s_requiredArtifactNames.Length
            && s_requiredArtifactNames.All(names.Contains);
    }

    /// <summary>
    /// 验证写入回调返回的文件名精确覆盖 v3 固定工件集合，拒绝空值、重复、遗漏和额外文件。
    /// </summary>
    /// <param name="paths">写入回调声明的相对文件名。</param>
    /// <returns>文件名集合可安全生成 v3 清单时返回 <see langword="true"/>。</returns>
    private static bool HasRequiredArtifactNameSet(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count != s_requiredArtifactNames.Length)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !names.Add(path))
            {
                return false;
            }
        }

        return s_requiredArtifactNames.All(names.Contains);
    }

    /// <summary>
    /// 将对象、类型、正反向 CSR 及根证据写入固定命名工件，并返回可校验清单条目。
    /// </summary>
    private static async Task<IReadOnlyList<string>> WriteArtifactsAsync(
        string directory,
        SnapshotIndex.SnapshotIndexArtifactData data,
        CancellationToken cancellationToken)
    {
        var types = data.Objects.Select(row => row.Type).Distinct().OrderBy(type => type.TypeName, StringComparer.Ordinal).ToArray();
        var typeIds = types.Select((type, index) => (type, index)).ToDictionary(pair => pair.type, pair => pair.index);
        var objectIdByAddress = data.Objects.Select((row, index) => (row.Address, index)).ToDictionary(pair => pair.Address, pair => pair.index);
        var rootRecords = data.Roots
            .Where(root => objectIdByAddress.ContainsKey(root.ObjectAddress)
                && !root.Root.Flags.HasFlag(MemoryRootFlags.WeakReference))
            .Select(root => new HeapIndexRootEvidence(objectIdByAddress[root.ObjectAddress], root.Root))
            .OrderBy(root => root.ObjectId)
            .ThenBy(root => GetRootPriority(root.Root))
            .ToArray();
        var paths = new List<string>();
        await WriteBinaryAsync(Path.Combine(directory, "types.bin"), writer =>
        {
            writer.Write(types.Length);
            foreach (var type in types)
            {
                writer.Write(type.TypeName);
                writer.Write(type.AssemblyName ?? string.Empty);
            }
        }, cancellationToken).ConfigureAwait(false);
        paths.Add("types.bin");
        await WriteJsonAsync(Path.Combine(directory, "type-summary.bin"), data.Objects.GroupBy(row => row.Type).Select(group => new MemoryTypeSummary(group.Key, group.LongCount(), group.Sum(row => row.SizeBytes))).ToArray(), cancellationToken).ConfigureAwait(false);
        paths.Add("type-summary.bin");
        await WriteBinaryAsync(Path.Combine(directory, "objects.bin"), writer =>
        {
            foreach (var row in data.Objects)
            {
                writer.Write(row.Address);
                writer.Write(typeIds[row.Type]);
                writer.Write(row.SizeBytes);
            }
        }, cancellationToken).ConfigureAwait(false);
        paths.Add("objects.bin");
        var unorderedAddressIdsPath = Path.Combine(directory, "address-to-id.unsorted.bin");
        await WriteBinaryAsync(unorderedAddressIdsPath, writer =>
        {
            foreach (var item in objectIdByAddress)
            {
                writer.Write(item.Key);
                writer.Write(item.Value);
            }
        }, cancellationToken).ConfigureAwait(false);
        await HeapIndexExternalSorter.SortAddressIdRecordsAsync(
            unorderedAddressIdsPath,
            Path.Combine(directory, "address-to-id.bin"),
            directory,
            maximumChunkBytes: HeapIndexResourcePolicy.GetExternalSortChunkBytes(),
            cancellationToken).ConfigureAwait(false);
        File.Delete(unorderedAddressIdsPath);
        paths.Add("address-to-id.bin");
        await WriteBinaryAsync(Path.Combine(directory, "objects-by-type.bin"), writer =>
        {
            foreach (var type in types)
            {
                foreach (var item in data.Objects.Select((row, index) => (row, index)).Where(item => item.row.Type == type))
                {
                    writer.Write(item.index);
                }
            }
        }, cancellationToken).ConfigureAwait(false);
        paths.Add("objects-by-type.bin");
        var unorderedEdgesPath = Path.Combine(directory, "edges.unsorted.bin");
        var forwardEdgesPath = Path.Combine(directory, "edges.forward.bin");
        var reverseEdgesPath = Path.Combine(directory, "edges.reverse.bin");
        await WriteBinaryAsync(unorderedEdgesPath, writer =>
        {
            foreach (var pair in data.Edges)
            {
                if (!objectIdByAddress.TryGetValue(pair.Key, out var source))
                {
                    continue;
                }

                foreach (var targetAddress in pair.Value)
                {
                    if (objectIdByAddress.TryGetValue(targetAddress, out var target))
                    {
                        writer.Write(source);
                        writer.Write(target);
                    }
                }
            }
        }, cancellationToken).ConfigureAwait(false);
        await HeapIndexExternalSorter.SortObjectIdEdgeRecordsAsync(
            unorderedEdgesPath,
            forwardEdgesPath,
            directory,
            maximumChunkBytes: HeapIndexResourcePolicy.GetExternalSortChunkBytes(),
            sortByTarget: false,
            cancellationToken).ConfigureAwait(false);
        await HeapIndexExternalSorter.SortObjectIdEdgeRecordsAsync(
            unorderedEdgesPath,
            reverseEdgesPath,
            directory,
            maximumChunkBytes: HeapIndexResourcePolicy.GetExternalSortChunkBytes(),
            sortByTarget: true,
            cancellationToken).ConfigureAwait(false);
        await Task.Run(
            () => WriteCsrFromSortedEdges(directory, "forward", forwardEdgesPath, data.Objects.Count, sortByTarget: false, cancellationToken),
            CancellationToken.None).ConfigureAwait(false);
        paths.AddRange(["forward-offsets.bin", "forward-targets.bin"]);
        await Task.Run(
            () => WriteCsrFromSortedEdges(directory, "reverse", reverseEdgesPath, data.Objects.Count, sortByTarget: true, cancellationToken),
            CancellationToken.None).ConfigureAwait(false);
        File.Delete(unorderedEdgesPath);
        File.Delete(forwardEdgesPath);
        File.Delete(reverseEdgesPath);
        paths.AddRange(["reverse-offsets.bin", "reverse-targets.bin"]);
        await WriteRootEvidenceArtifactsAsync(directory, data.Objects.Count, rootRecords, cancellationToken).ConfigureAwait(false);
        paths.AddRange(["roots-by-object.bin", "root-evidence.bin"]);
        return paths;
    }

    /// <summary>
    /// 为回调声明的每个工件生成清单记录，并拒绝不存在或跳出临时目录的路径。
    /// </summary>
    private static async Task<IReadOnlyList<HeapIndexArtifactFile>> CreateManifestFilesAsync(
        string directory,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var results = new List<HeapIndexArtifactFile>(paths.Count);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.IsPathRooted(path) || path.Contains("..", StringComparison.Ordinal))
            {
                throw new InvalidDataException("索引工件路径无效。");
            }

            var fullPath = Path.Combine(directory, path);
            if (!File.Exists(fullPath))
            {
                throw new InvalidDataException("索引工件未写入完成。");
            }

            var hash = await ComputeHashAsync(fullPath, cancellationToken).ConfigureAwait(false);
            results.Add(new HeapIndexArtifactFile(path, new FileInfo(fullPath).Length, Convert.ToHexString(hash)));
        }

        return results;
    }

    /// <summary>
    /// 异步写入一个小型二进制工件并在回调内保持固定顺序编码。
    /// </summary>
    private static async Task WriteBinaryAsync(string path, Action<BinaryWriter> write, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        write(writer);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 将非弱根证据按连续 objectId 编码为偏移表和变长记录流，使路径和派生查询只读取命中对象的证据。
    /// </summary>
    private static async Task WriteRootEvidenceArtifactsAsync(
        string directory,
        int objectCount,
        HeapIndexRootEvidence[] records,
        CancellationToken cancellationToken)
    {
        var offsetsPath = Path.Combine(directory, "roots-by-object.bin");
        var evidencePath = Path.Combine(directory, "root-evidence.bin");
        await using var offsetsStream = new FileStream(offsetsPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var evidenceStream = new FileStream(evidencePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var offsets = new BinaryWriter(offsetsStream, System.Text.Encoding.UTF8, leaveOpen: true);
        using var evidence = new BinaryWriter(evidenceStream, System.Text.Encoding.UTF8, leaveOpen: true);
        var recordIndex = 0;
        for (var objectId = 0; objectId < objectCount; objectId++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            offsets.Write(evidenceStream.Position);
            while (recordIndex < records.Length && records[recordIndex].ObjectId == objectId)
            {
                WriteRootEvidence(evidence, records[recordIndex].Root);
                recordIndex++;
            }
        }

        if (recordIndex != records.Length)
        {
            throw new InvalidDataException("根证据对象标识超出对象工件范围。");
        }

        offsets.Write(evidenceStream.Position);
        await offsetsStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await evidenceStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 以稳定的紧凑二进制格式写入单条根证据；字符串使用 -1 长度表示 null，避免伪造函数或模块名称。
    /// </summary>
    private static void WriteRootEvidence(BinaryWriter writer, MemoryRetentionRoot root)
    {
        writer.Write((byte)root.Kind);
        writer.Write((int)root.Flags);
        WriteNullableString(writer, root.FunctionName);
        WriteNullableString(writer, root.ModuleName);
    }

    /// <summary>
    /// 写入可空 UTF-8 字符串，长度表示字节数而非字符数以保证跨语言确定性。
    /// </summary>
    private static void WriteNullableString(BinaryWriter writer, string? value)
    {
        if (value is null)
        {
            writer.Write(-1);
            return;
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    /// <summary>
    /// 对多个根证据使用与公开保留路径一致的稳定优先级，避免磁盘和内存索引排序分叉。
    /// </summary>
    private static int GetRootPriority(MemoryRetentionRoot root) => root.Kind switch
    {
        MemoryRootKind.Stack when root.FunctionName is not null => 0,
        MemoryRootKind.Stack => 1,
        MemoryRootKind.Handle => 2,
        MemoryRootKind.Finalizer => 3,
        MemoryRootKind.Other => 4,
        _ => 5
    };

    /// <summary>
    /// 从按 source/target 或 target/source 排序的边文件顺序输出 CSR，并在相邻重复边处去重。
    /// </summary>
    internal static void WriteCsrFromSortedEdges(
        string directory,
        string prefix,
        string sortedEdgesPath,
        int objectCount,
        bool sortByTarget,
        CancellationToken cancellationToken)
    {
        using var offsetsStream = new FileStream(Path.Combine(directory, $"{prefix}-offsets.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
        using var offsets = new BinaryWriter(offsetsStream);
        using var targetsStream = new FileStream(Path.Combine(directory, $"{prefix}-targets.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
        using var targets = new BinaryWriter(targetsStream);
        using var reader = new BinaryReader(File.OpenRead(sortedEdgesPath));
        var hasPending = TryReadEdge(reader, out var pendingSource, out var pendingTarget);
        long targetOffset = 0;
        offsets.Write(targetOffset);
        for (var objectId = 0; objectId < objectCount; objectId++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previousOther = -1;
            while (hasPending && (sortByTarget ? pendingTarget : pendingSource) == objectId)
            {
                var other = sortByTarget ? pendingSource : pendingTarget;
                if (other != previousOther)
                {
                    targets.Write(other);
                    targetOffset++;
                    previousOther = other;
                }

                hasPending = TryReadEdge(reader, out pendingSource, out pendingTarget);
            }

            offsets.Write(targetOffset);
        }

        if (hasPending)
        {
            throw new InvalidDataException("排序边工件包含越界 objectId。");
        }
    }

    /// <summary>
    /// 从固定宽度边工件读取下一条 source/target 记录，并验证其完整性。
    /// </summary>
    private static bool TryReadEdge(BinaryReader reader, out int source, out int target)
    {
        if (reader.BaseStream.Position == reader.BaseStream.Length)
        {
            source = default;
            target = default;
            return false;
        }

        if (reader.BaseStream.Length - reader.BaseStream.Position < sizeof(int) * 2)
        {
            throw new InvalidDataException("排序边工件截断。");
        }

        source = reader.ReadInt32();
        target = reader.ReadInt32();
        return true;
    }

    /// <summary>
    /// 使用异步流写入 JSON 工件，扩展名保持 .bin 以表明它属于索引工件族而非用户文档。
    /// </summary>
    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(stream, value, cancellationToken: cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 以受控文件句柄计算工件摘要，确保构建结束后临时目录可被清理或原子提升。
    /// </summary>
    private static async Task<byte[]> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 表示索引目录的版本、完成标记和完整性文件清单。
    /// </summary>
    private sealed record HeapIndexManifest(int Version, bool Completed, IReadOnlyList<HeapIndexArtifactFile> Files);

    /// <summary>
    /// 表示单个索引工件的长度和 SHA-256 完整性信息。
    /// </summary>
    private sealed record HeapIndexArtifactFile(string Name, long Length, string Sha256);

}

/// <summary>
/// 将根证据关联到连续对象标识，避免路径读取时扫描全部对象元数据。
/// </summary>
internal sealed record HeapIndexRootEvidence(int ObjectId, MemoryRetentionRoot Root);
