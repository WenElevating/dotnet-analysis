using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 集中生成快照目录、堆文件和 JSON 清单路径。
/// </summary>
public sealed class SnapshotStorageLayout
{
    /// <summary>
    /// 获取由应用管理的本地数据快照目录。
    /// </summary>
    public static string GetDefaultRootDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DotnetAnalysis",
        "Snapshots");

    /// <summary>
    /// 创建并规范化快照根目录。
    /// </summary>
    /// <param name="rootDirectory">快照文件的根目录。</param>
    public SnapshotStorageLayout(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Snapshot root is required.", nameof(rootDirectory));
        }

        RootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(RootDirectory);
    }

    /// <summary>
    /// 快照文件和清单的持久化根目录。
    /// </summary>
    public string RootDirectory { get; }

    /// <summary>
    /// 获取指定快照的专属目录。
    /// </summary>
    public string GetSnapshotDirectory(MemorySnapshotId snapshotId) =>
        Path.Combine(RootDirectory, snapshotId.ToString());

    /// <summary>
    /// 生成根目录下的隐藏临时 .gcdump 路径。
    /// </summary>
    public string TemporaryPath(Guid id) => Path.Combine(RootDirectory, $".{id:N}.tmp.gcdump");

    /// <summary>
    /// 生成根目录下的可见 .gcdump 路径。
    /// </summary>
    public string VisiblePath(Guid id) => Path.Combine(RootDirectory, $"{id:N}.gcdump");

    /// <summary>
    /// 获取指定快照的临时堆文件路径。
    /// </summary>
    public string GetTemporaryDumpPath(MemorySnapshotId snapshotId) =>
        Path.Combine(GetSnapshotDirectory(snapshotId), "capture.tmp.gcdump");

    /// <summary>
    /// 获取指定快照的最终堆文件路径。
    /// </summary>
    public string GetFinalDumpPath(MemorySnapshotId snapshotId) =>
        Path.Combine(GetSnapshotDirectory(snapshotId), $"{snapshotId}.gcdump");

    /// <summary>
    /// 获取指定快照的临时保留分析堆文件路径。
    /// </summary>
    /// <param name="snapshotId">快照稳定标识。</param>
    /// <returns>仅在写入成功前存在的专用格式路径。</returns>
    public string GetTemporaryRetentionHeapPath(MemorySnapshotId snapshotId) =>
        Path.Combine(GetSnapshotDirectory(snapshotId), "capture.tmp.retentionheap");

    /// <summary>
    /// 获取指定快照的最终保留分析堆文件路径。
    /// </summary>
    /// <param name="snapshotId">快照稳定标识。</param>
    /// <returns>带有独立文件扩展名的最终保留分析快照路径。</returns>
    public string GetFinalRetentionHeapPath(MemorySnapshotId snapshotId) =>
        Path.Combine(GetSnapshotDirectory(snapshotId), $"{snapshotId}.retentionheap");

    /// <summary>
    /// 获取指定快照的临时 JSON 清单路径。
    /// </summary>
    public string GetTemporaryManifestPath(MemorySnapshotId snapshotId) =>
        Path.Combine(GetSnapshotDirectory(snapshotId), "capture.tmp.json");

    /// <summary>
    /// 获取指定快照的最终 JSON 清单路径。
    /// </summary>
    public string GetFinalManifestPath(MemorySnapshotId snapshotId) =>
        Path.Combine(GetSnapshotDirectory(snapshotId), "snapshot.json");

    /// <summary>
    /// 获取指定快照基础堆索引的不可变工件目录；目录仅在完整验证后才会被原子发布。
    /// </summary>
    /// <param name="snapshotId">快照稳定标识。</param>
    /// <returns>当前版本堆索引工件根目录。</returns>
    public string GetHeapIndexDirectory(MemorySnapshotId snapshotId) =>
        Path.Combine(GetSnapshotDirectory(snapshotId), ".heapidx");
}
