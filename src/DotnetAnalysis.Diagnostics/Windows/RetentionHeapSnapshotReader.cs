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
    Task<SnapshotIndex> IIndexedMemorySnapshotReader.ReadIndexAsync(string filePath) =>
        RetentionHeapSnapshot.ReadIndexAsync(filePath, CancellationToken.None);
}
