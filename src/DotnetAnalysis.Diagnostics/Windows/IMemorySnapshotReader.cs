using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 抽象读取不同内存快照格式的统一查询能力。
/// </summary>
public interface IMemorySnapshotReader
{
    /// <summary>
    /// 判断读取器能否处理指定快照文件。
    /// </summary>
    bool CanRead(string filePath);
    /// <summary>
    /// 异步读取快照中的按类型聚合统计。
    /// </summary>
    Task<IReadOnlyList<MemoryTypeSummary>> ReadTypeSummariesAsync(string filePath, CancellationToken cancellationToken);
    /// <summary>
    /// 异步读取指定类型的对象实例。
    /// </summary>
    Task<IReadOnlyList<MemoryObjectInfo>> ReadObjectsAsync(string filePath, TypeIdentity type, CancellationToken cancellationToken);
    /// <summary>
    /// 异步读取到指定对象地址的引用路径。
    /// </summary>
    Task<MemoryReferencePath?> ReadReferencePathAsync(string filePath, ulong objectAddress, CancellationToken cancellationToken);
}
