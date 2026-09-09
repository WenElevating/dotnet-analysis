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
    /// <param name="snapshot">要分析的已保存或已导入快照。</param>
    /// <param name="cancellationToken">取消当前等待或解析操作的令牌。</param>
    /// <returns>包含类型统计、对象读取方式及分配概要的分析结果。</returns>
    Task<MemorySnapshotAnalysis> AnalyzeAsync(
        MemorySnapshot snapshot,
        CancellationToken cancellationToken);

    /// <summary>
    /// 读取指定类型的全部对象实例；大型快照应改用 <see cref="GetObjectsPageAsync"/>。
    /// </summary>
    /// <param name="snapshot">待查询的快照。</param>
    /// <param name="type">要读取的对象类型。</param>
    /// <param name="cancellationToken">取消当前等待或投影操作的令牌。</param>
    /// <returns>指定类型的全部对象实例。</returns>
    /// <exception cref="DiagnosticsException">快照对象数达到 100,000 时，以 <see cref="DiagnosticsErrorCode.SnapshotTooLargeForFullEnumeration"/> 引发，调用方应改用 <see cref="GetObjectsPageAsync"/>。</exception>
    Task<IReadOnlyList<MemoryObjectInfo>> GetObjectsAsync(
        MemorySnapshot snapshot,
        TypeIdentity type,
        CancellationToken cancellationToken);

    /// <summary>
    /// 按页读取指定类型的对象实例。
    /// </summary>
    /// <param name="snapshot">待查询的快照。</param>
    /// <param name="type">要读取的对象类型。</param>
    /// <param name="offset">相对于该类型对象列表的零基偏移量。</param>
    /// <param name="pageSize">每页对象数，范围为 1 至 1000。</param>
    /// <param name="cancellationToken">取消当前等待或投影操作的令牌。</param>
    /// <returns>包含对象、总数和后续页信息的分页结果。</returns>
    /// <exception cref="ArgumentOutOfRangeException">偏移量或页大小不在有效范围时引发。</exception>
    Task<MemoryObjectPage> GetObjectsPageAsync(
        MemorySnapshot snapshot,
        TypeIdentity type,
        int offset,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// 读取到目标对象的引用路径；不存在时返回空。
    /// </summary>
    /// <param name="snapshot">待查询的快照。</param>
    /// <param name="objectAddress">目标对象在快照中的地址。</param>
    /// <param name="cancellationToken">取消当前等待或路径投影操作的令牌。</param>
    /// <returns>从根或可达链起点到目标对象的路径；对象未知时返回空。</returns>
    Task<MemoryReferencePath?> GetReferencePathAsync(
        MemorySnapshot snapshot,
        ulong objectAddress,
        CancellationToken cancellationToken);

    /// <summary>
    /// 查询指定对象最多 16 条包含 GC 根证据的保留路径。
    /// </summary>
    /// <param name="snapshot">待查询的快照。</param>
    /// <param name="objectAddress">目标对象在快照中的地址。</param>
    /// <param name="maxPathCount">最多返回的路径数，范围为 1 至 16。</param>
    /// <param name="cancellationToken">取消当前等待或路径投影操作的令牌。</param>
    /// <returns>按函数已验证栈根、其他栈根、Handle、Finalizer、Other/Unknown 顺序排列的保留路径；对象未知或不可达时返回空。</returns>
    /// <exception cref="ArgumentOutOfRangeException">最大路径数不在 1 至 16 范围内时引发。</exception>
    Task<MemoryRetentionPathResult?> GetRetentionPathsAsync(
        MemorySnapshot snapshot,
        ulong objectAddress,
        int maxPathCount,
        CancellationToken cancellationToken) => throw new NotSupportedException("当前快照分析服务不支持保留路径查询。");
}
