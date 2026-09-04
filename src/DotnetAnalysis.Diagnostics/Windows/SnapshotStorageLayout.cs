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
    /// 获取指定快照的临时 JSON 清单路径。
    /// </summary>
    public string GetTemporaryManifestPath(MemorySnapshotId snapshotId) =>
        Path.Combine(GetSnapshotDirectory(snapshotId), "capture.tmp.json");

    /// <summary>
    /// 获取指定快照的最终 JSON 清单路径。
    /// </summary>
    public string GetFinalManifestPath(MemorySnapshotId snapshotId) =>
        Path.Combine(GetSnapshotDirectory(snapshotId), "snapshot.json");
}
