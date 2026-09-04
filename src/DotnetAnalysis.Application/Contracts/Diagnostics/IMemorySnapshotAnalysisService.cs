using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Application.Contracts.Diagnostics;

/// <summary>
/// 提供快照类型统计、对象列表和引用路径查询的应用层契约。
/// </summary>
public interface IMemorySnapshotAnalysisService
{
    /// <summary>
    /// 分析快照并返回就绪状态及类型统计。
    /// </summary>
    Task<MemorySnapshotAnalysis> AnalyzeAsync(
        MemorySnapshot snapshot,
        CancellationToken cancellationToken);

    /// <summary>
    /// 读取指定类型的对象实例。
    /// </summary>
    Task<IReadOnlyList<MemoryObjectInfo>> GetObjectsAsync(
        MemorySnapshot snapshot,
        TypeIdentity type,
        CancellationToken cancellationToken);

    /// <summary>
    /// 读取到目标对象的引用路径；不存在时返回空。
    /// </summary>
    Task<MemoryReferencePath?> GetReferencePathAsync(
        MemorySnapshot snapshot,
        ulong objectAddress,
        CancellationToken cancellationToken);
}
