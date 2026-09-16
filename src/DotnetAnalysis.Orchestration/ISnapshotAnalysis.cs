using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Orchestration;

/// <summary>
/// 提供单个正式快照的基础查询、分页查询和派生分析入口。
/// </summary>
public interface ISnapshotAnalysis : IAsyncDisposable
{
    /// <summary>当前分析绑定的正式快照。</summary>
    MemorySnapshot Snapshot { get; }

    /// <summary>分析快照并缓存基础分析结果；调用取消只取消当前等待。</summary>
    /// <param name="cancellationToken">取消当前调用等待的令牌。</param>
    /// <returns>基础类型统计和质量信息。</returns>
    Task<MemorySnapshotAnalysis> AnalyzeAsync(CancellationToken cancellationToken);

    /// <summary>读取指定类型的全部对象。</summary>
    /// <param name="type">对象类型。</param>
    /// <param name="cancellationToken">取消当前查询的令牌。</param>
    /// <returns>对象集合。</returns>
    Task<IReadOnlyList<MemoryObjectInfo>> GetObjectsAsync(TypeIdentity type, CancellationToken cancellationToken);

    /// <summary>按页读取指定类型的对象。</summary>
    /// <param name="type">对象类型。</param>
    /// <param name="offset">零基偏移量。</param>
    /// <param name="pageSize">页大小，范围为 1 至 1000。</param>
    /// <param name="cancellationToken">取消当前查询的令牌。</param>
    /// <returns>对象分页结果。</returns>
    Task<MemoryObjectPage> GetObjectsPageAsync(TypeIdentity type, int offset, int pageSize, CancellationToken cancellationToken);

    /// <summary>读取目标对象的引用路径。</summary>
    /// <param name="objectAddress">目标对象地址。</param>
    /// <param name="cancellationToken">取消当前查询的令牌。</param>
    /// <returns>引用路径；不存在时为空。</returns>
    Task<MemoryReferencePath?> GetReferencePathAsync(ulong objectAddress, CancellationToken cancellationToken);

    /// <summary>读取目标对象的保留路径。</summary>
    /// <param name="objectAddress">目标对象地址。</param>
    /// <param name="maxPathCount">最大路径数，范围为 1 至 16。</param>
    /// <param name="cancellationToken">取消当前查询的令牌。</param>
    /// <returns>保留路径结果；不存在时为空。</returns>
    Task<MemoryRetentionPathResult?> GetRetentionPathsAsync(ulong objectAddress, int maxPathCount, CancellationToken cancellationToken);

    /// <summary>按页读取支配树派生结果。</summary>
    /// <param name="offset">零基偏移量。</param>
    /// <param name="pageSize">页大小，范围为 1 至 1000。</param>
    /// <param name="cancellationToken">取消当前查询的令牌。</param>
    /// <returns>支配树分页结果。</returns>
    Task<MemoryDominatorPage> GetDominatorPageAsync(int offset, int pageSize, CancellationToken cancellationToken);

    /// <summary>创建独立的基线与候选快照比较。</summary>
    /// <param name="candidate">候选快照分析。</param>
    /// <returns>独立比较对象。</returns>
    SnapshotComparison CompareWith(ISnapshotAnalysis candidate);
}
