using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 协调快照存储、格式读取器和核心分析结果的 Windows 实现。
/// </summary>
internal sealed class DiagnosticsMemorySnapshotAnalysisService : IMemorySnapshotAnalysisService, IDisposable
{
    private readonly ImportedSnapshotCatalog _catalog;
    private readonly MemorySnapshotStore _store;
    private readonly MemorySnapshotReaderRegistry _registry;
    private readonly HeapIndexHandleCache _indexCache = new();
    private readonly HeapIndexRouter _indexRouter;

    /// <summary>
    /// 创建连接快照目录、持久化存储和格式读取器的分析服务。
    /// </summary>
    public DiagnosticsMemorySnapshotAnalysisService(
        ImportedSnapshotCatalog catalog,
        MemorySnapshotStore store,
        MemorySnapshotReaderRegistry registry,
        SnapshotStorageLayout layout)
    {
        _catalog = catalog;
        _store = store;
        _registry = registry;
        _indexRouter = new HeapIndexRouter(layout);
    }

    /// <summary>
    /// 释放当前快照的映射索引句柄；不会删除原始快照或已验证工件。
    /// </summary>
    public void Dispose() => _indexCache.Dispose();

    /// <summary>
    /// 读取快照中的类型统计和关联分配概要，并将可分析快照推进为就绪状态。
    /// </summary>
    public async Task<MemorySnapshotAnalysis> AnalyzeAsync(MemorySnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var path = await ResolvePathAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);

        using var index = await GetIndexAsync(snapshot, path, cancellationToken).ConfigureAwait(false);
        var endedAtUtc = snapshot.CapturedAtUtc ?? snapshot.RequestedAtUtc;
        if (endedAtUtc < snapshot.RequestedAtUtc)
        {
            // Imported files can carry a filesystem timestamp earlier than
            // the moment the user opened them.  The Core contract requires a
            // non-decreasing interval, so clamp the quality-only interval.
            endedAtUtc = snapshot.RequestedAtUtc;
        }

        var allocationProfile = AllocationProfile.NotAvailable(snapshot.RequestedAtUtc, endedAtUtc);
        if (snapshot.Origin == MemorySnapshotOrigin.Captured)
        {
            var stored = await _store.ResolveAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);
            allocationProfile = stored.AllocationProfile;
        }

        return new MemorySnapshotAnalysis(
            snapshot.State == MemorySnapshotState.Analyzing
                ? snapshot.MoveTo(MemorySnapshotState.Ready)
                : snapshot,
            index.TypeSummaries,
            allocationProfile,
            index.ObjectAccessMode);
    }

    /// <summary>
    /// 读取指定类型的对象列表，不泄漏底层快照格式实现。
    /// </summary>
    public Task<IReadOnlyList<MemoryObjectInfo>> GetObjectsAsync(MemorySnapshot snapshot, TypeIdentity type, CancellationToken cancellationToken)
    {
        return ReadObjectsCoreAsync(snapshot, type, cancellationToken);
    }

    /// <summary>
    /// 按页读取指定类型的对象列表。
    /// </summary>
    public async Task<MemoryObjectPage> GetObjectsPageAsync(
        MemorySnapshot snapshot,
        TypeIdentity type,
        int offset,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 1000);

        var path = await ResolvePathAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);
        using var index = await GetIndexAsync(snapshot, path, cancellationToken).ConfigureAwait(false);
        return index.GetPage(type, offset, pageSize);
    }

    /// <summary>
    /// 读取到目标对象的可用引用路径；不可达或未知对象返回 <see langword="null"/>。
    /// </summary>
    public Task<MemoryReferencePath?> GetReferencePathAsync(MemorySnapshot snapshot, ulong objectAddress, CancellationToken cancellationToken)
    {
        return ReadReferencePathCoreAsync(snapshot, objectAddress, cancellationToken);
    }

    /// <summary>
    /// 读取到目标对象的保留路径；GCDump 只能提供 Unknown 根证据，Profiler 快照可提供更具体证据。
    /// </summary>
    public Task<MemoryRetentionPathResult?> GetRetentionPathsAsync(
        MemorySnapshot snapshot,
        ulong objectAddress,
        int maxPathCount,
        CancellationToken cancellationToken)
    {
        return ReadRetentionPathsCoreAsync(snapshot, objectAddress, maxPathCount, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MemoryDominatorPage> GetDominatorPageAsync(
        MemorySnapshot snapshot,
        int offset,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 1000);
        var path = await ResolvePathAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);
        using var index = await GetIndexAsync(snapshot, path, cancellationToken).ConfigureAwait(false);
        try
        {
            return await index.GetDominatorPageAsync(offset, pageSize, cancellationToken).ConfigureAwait(false);
        }
        catch (DiagnosticsException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.DerivedAnalysisUnavailable,
                "无法构建快照支配树分析。",
                exception);
        }
    }

    /// <inheritdoc />
    public async Task<MemorySnapshotComparison> CompareSnapshotsAsync(
        MemorySnapshot baselineSnapshot,
        MemorySnapshot candidateSnapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baselineSnapshot);
        ArgumentNullException.ThrowIfNull(candidateSnapshot);
        var baselinePath = await ResolvePathAsync(baselineSnapshot.Id, cancellationToken).ConfigureAwait(false);
        var candidatePath = await ResolvePathAsync(candidateSnapshot.Id, cancellationToken).ConfigureAwait(false);
        using var baselineIndex = await GetIndexAsync(baselineSnapshot, baselinePath, cancellationToken).ConfigureAwait(false);
        var baselineSummaries = baselineIndex.TypeSummaries.ToArray();
        using var candidateIndex = await GetIndexAsync(candidateSnapshot, candidatePath, cancellationToken).ConfigureAwait(false);
        var candidateSummaries = candidateIndex.TypeSummaries.ToArray();
        var baselineByType = baselineSummaries.ToDictionary(summary => summary.Type);
        var candidateByType = candidateSummaries.ToDictionary(summary => summary.Type);
        var summaries = baselineByType.Keys
            .Concat(candidateByType.Keys)
            .Distinct()
            .Select(type =>
            {
                baselineByType.TryGetValue(type, out var baseline);
                candidateByType.TryGetValue(type, out var candidate);
                return new MemoryTypeGrowth(
                    type,
                    baseline?.ObjectCount ?? 0,
                    candidate?.ObjectCount ?? 0,
                    baseline?.TotalSizeBytes ?? 0,
                    candidate?.TotalSizeBytes ?? 0);
            })
            .OrderByDescending(growth => growth.ShallowSizeGrowthBytes)
            .ThenByDescending(growth => growth.ObjectCountGrowth)
            .ThenBy(growth => growth.Type.TypeName, StringComparer.Ordinal)
            .ToArray();
        return new MemorySnapshotComparison(baselineSnapshot.Id, candidateSnapshot.Id, summaries);
    }

    /// <summary>
    /// 从内存目录或持久化清单解析快照文件路径。
    /// </summary>
    private async Task<string> ResolvePathAsync(MemorySnapshotId snapshotId, CancellationToken cancellationToken)
    {
        if (_catalog.TryResolve(snapshotId, out var path))
        {
            return path;
        }

        var resolved = await _store.ResolveAsync(snapshotId, cancellationToken).ConfigureAwait(false);
        _catalog.Register(snapshotId, resolved.FilePath);
        return resolved.FilePath;
    }

    /// <summary>
    /// 解析快照路径并委托对应读取器返回对象列表。
    /// </summary>
    private async Task<IReadOnlyList<MemoryObjectInfo>> ReadObjectsCoreAsync(
        MemorySnapshot snapshot,
        TypeIdentity type,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(type);
        var path = await ResolvePathAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);
        using var index = await GetIndexAsync(snapshot, path, cancellationToken).ConfigureAwait(false);
        return index.GetObjects(type);
    }

    /// <summary>
    /// 解析快照路径并委托对应读取器计算引用路径。
    /// </summary>
    private async Task<MemoryReferencePath?> ReadReferencePathCoreAsync(
        MemorySnapshot snapshot,
        ulong objectAddress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var path = await ResolvePathAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);
        using var index = await GetIndexAsync(snapshot, path, cancellationToken).ConfigureAwait(false);
        return index.GetReferencePath(objectAddress, cancellationToken);
    }

    /// <summary>
    /// 解析快照路径并委托索引计算指定数量的 GC 根保留路径。
    /// </summary>
    private async Task<MemoryRetentionPathResult?> ReadRetentionPathsCoreAsync(
        MemorySnapshot snapshot,
        ulong objectAddress,
        int maxPathCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPathCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPathCount, 16);
        var path = await ResolvePathAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);
        using var index = await GetIndexAsync(snapshot, path, cancellationToken).ConfigureAwait(false);
        return await Task.Run(
            () => index.GetRetentionPaths(objectAddress, maxPathCount, cancellationToken),
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// 使用当前快照的单飞缓存读取紧凑对象索引。
    /// </summary>
    private Task<HeapIndexHandle> GetIndexAsync(
        MemorySnapshot snapshot,
        string path,
        CancellationToken cancellationToken)
    {
        var reader = _registry.Resolve(path);
        if (reader is not IIndexedMemorySnapshotReader indexedReader)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.SnapshotFormatNotSupported,
                "The snapshot reader does not support indexed object analysis.");
        }

        return _indexCache.GetAsync(
            snapshot.Id,
            async () =>
            {
                return await indexedReader.BuildIndexAsync(
                    snapshot.Id,
                    path,
                    _indexRouter,
                    CancellationToken.None).ConfigureAwait(false);
            },
            cancellationToken);
    }
}
