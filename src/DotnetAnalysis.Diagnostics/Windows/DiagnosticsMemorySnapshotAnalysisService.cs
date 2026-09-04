using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 协调快照存储、格式读取器和核心分析结果的 Windows 实现。
/// </summary>
internal sealed class DiagnosticsMemorySnapshotAnalysisService : IMemorySnapshotAnalysisService
{
    private readonly ImportedSnapshotCatalog _catalog;
    private readonly MemorySnapshotStore _store;
    private readonly MemorySnapshotReaderRegistry _registry;

    /// <summary>
    /// 创建连接快照目录、持久化存储和格式读取器的分析服务。
    /// </summary>
    public DiagnosticsMemorySnapshotAnalysisService(
        ImportedSnapshotCatalog catalog,
        MemorySnapshotStore store,
        MemorySnapshotReaderRegistry registry)
    {
        _catalog = catalog;
        _store = store;
        _registry = registry;
    }

    /// <summary>
    /// 读取快照中的类型统计和关联分配概要，并将可分析快照推进为就绪状态。
    /// </summary>
    public async Task<MemorySnapshotAnalysis> AnalyzeAsync(MemorySnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var path = await ResolvePathAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);

        var reader = _registry.Resolve(path);
        var types = await reader.ReadTypeSummariesAsync(path, cancellationToken).ConfigureAwait(false);
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
            types,
            allocationProfile);
    }

    /// <summary>
    /// 读取指定类型的对象列表，不泄漏底层快照格式实现。
    /// </summary>
    public Task<IReadOnlyList<MemoryObjectInfo>> GetObjectsAsync(MemorySnapshot snapshot, TypeIdentity type, CancellationToken cancellationToken)
    {
        return ReadObjectsCoreAsync(snapshot, type, cancellationToken);
    }

    /// <summary>
    /// 读取到目标对象的可用引用路径；不可达或未知对象返回 <see langword="null"/>。
    /// </summary>
    public Task<MemoryReferencePath?> GetReferencePathAsync(MemorySnapshot snapshot, ulong objectAddress, CancellationToken cancellationToken)
    {
        return ReadReferencePathCoreAsync(snapshot, objectAddress, cancellationToken);
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
        var path = await ResolvePathAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);
        return await _registry.Resolve(path).ReadObjectsAsync(path, type, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 解析快照路径并委托对应读取器计算引用路径。
    /// </summary>
    private async Task<MemoryReferencePath?> ReadReferencePathCoreAsync(
        MemorySnapshot snapshot,
        ulong objectAddress,
        CancellationToken cancellationToken)
    {
        var path = await ResolvePathAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);
        return await _registry.Resolve(path).ReadReferencePathAsync(path, objectAddress, cancellationToken).ConfigureAwait(false);
    }
}
