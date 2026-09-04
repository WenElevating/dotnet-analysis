using System.Collections.Concurrent;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 线程安全保存导入快照标识到本地路径的映射。
/// </summary>
public sealed class ImportedSnapshotCatalog
{
    private readonly ConcurrentDictionary<MemorySnapshotId, string> _paths = new();

    /// <summary>
    /// 登记快照标识与规范化本地文件路径的对应关系。
    /// </summary>
    public void Register(MemorySnapshotId id, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _paths[id] = Path.GetFullPath(path);
    }

    /// <summary>
    /// 尝试解析快照标识对应的已登记路径。
    /// </summary>
    public bool TryResolve(MemorySnapshotId id, out string path) => _paths.TryGetValue(id, out path!);

    /// <summary>
    /// 移除不再可用的快照路径映射。
    /// </summary>
    public void Remove(MemorySnapshotId id) => _paths.TryRemove(id, out _);
}
