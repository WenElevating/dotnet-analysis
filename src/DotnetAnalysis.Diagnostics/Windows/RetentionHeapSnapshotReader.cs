using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 读取由保留分析 Profiler 捕获的专用对象图快照。
/// </summary>
public sealed class RetentionHeapSnapshotReader : IIndexedMemorySnapshotReader
{
    /// <inheritdoc />
    public bool CanRead(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".retentionheap", StringComparison.OrdinalIgnoreCase)
        && RetentionHeapSnapshot.HasMagic(filePath);

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryTypeSummary>> ReadTypeSummariesAsync(string filePath, CancellationToken cancellationToken) =>
        (await RetentionHeapSnapshot.ReadIndexAsync(filePath, cancellationToken).ConfigureAwait(false)).TypeSummaries;

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryObjectInfo>> ReadObjectsAsync(string filePath, TypeIdentity type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(type);
        return (await RetentionHeapSnapshot.ReadIndexAsync(filePath, cancellationToken).ConfigureAwait(false)).GetObjects(type);
    }

    /// <inheritdoc />
    public async Task<MemoryReferencePath?> ReadReferencePathAsync(string filePath, ulong objectAddress, CancellationToken cancellationToken) =>
        (await RetentionHeapSnapshot.ReadIndexAsync(filePath, cancellationToken).ConfigureAwait(false)).GetReferencePath(objectAddress);

    /// <inheritdoc />
    Task<HeapIndexHandle> IIndexedMemorySnapshotReader.BuildIndexAsync(
        MemorySnapshotId snapshotId,
        string filePath,
        HeapIndexRouter router,
        CancellationToken cancellationToken) =>
        BuildIndexCoreAsync(snapshotId, filePath, router, cancellationToken);

    /// <summary>
    /// 通过保留快照解析器建立索引句柄；大图的直接流式工件构建在此内部边界实现。
    /// </summary>
    private static async Task<HeapIndexHandle> BuildIndexCoreAsync(
        MemorySnapshotId snapshotId,
        string filePath,
        HeapIndexRouter router,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(router);
        var existing = await router
            .TryOpenExistingMappedAsync(snapshotId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var counts = await RetentionHeapSnapshot.ReadCountsAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (router.ShouldUseMapped(counts.ObjectCount, counts.EdgeCount))
        {
            return await router.BuildMappedAsync(
                snapshotId,
                (directory, token) => HeapIndexArtifactStore.PublishAsync(
                    directory,
                    (temporaryDirectory, writeToken) => RetentionHeapSnapshot.WriteMappedArtifactsAsync(temporaryDirectory, filePath, writeToken),
                    HeapArtifactStorageGuard.EstimateIndexBuildPeakBytes(
                        counts.ObjectCount,
                        counts.EdgeCount,
                        new FileInfo(filePath).Length),
                    token),
                cancellationToken).ConfigureAwait(false);
        }

        var index = await RetentionHeapSnapshot.ReadIndexAsync(filePath, cancellationToken).ConfigureAwait(false);
        return await router.RouteAsync(snapshotId, index, cancellationToken).ConfigureAwait(false);
    }
}
