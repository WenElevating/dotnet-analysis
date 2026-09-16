using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Orchestration;

/// <summary>
/// 拥有正式快照顺序、选择状态和每个快照分析句柄的集合。
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "This public contract represents an orchestration snapshot collection.")]
public interface ISnapshotCollection : IAsyncDisposable
{
    /// <summary>按登记顺序返回正式快照的防御性副本。</summary>
    IReadOnlyList<MemorySnapshot> Snapshots { get; }

    /// <summary>当前选中的快照。</summary>
    MemorySnapshot? CurrentSnapshot { get; }

    /// <summary>当前比较基线快照。</summary>
    MemorySnapshot? BaselineSnapshot { get; }

    /// <summary>当前比较候选快照。</summary>
    MemorySnapshot? CandidateSnapshot { get; }

    /// <summary>等待捕获完成并登记 Diagnostics 返回的正式快照。</summary>
    /// <param name="capture">负责持久化并返回正式快照的操作。</param>
    /// <param name="cancellationToken">取消本次捕获的令牌。</param>
    /// <returns>已登记的快照；取消或失败时集合不变。</returns>
    Task<MemorySnapshot> AddCapturedAsync(Func<CancellationToken, Task<MemorySnapshot>> capture, CancellationToken cancellationToken);

    /// <summary>登记已由 Diagnostics 正式确认的导入快照。</summary>
    /// <param name="snapshot">已存在的正式快照描述。</param>
    /// <returns>登记后的快照。</returns>
    MemorySnapshot AddImported(MemorySnapshot snapshot);

    /// <summary>切换当前快照。</summary>
    /// <param name="snapshotId">快照标识。</param>
    void SelectCurrent(MemorySnapshotId snapshotId);

    /// <summary>设置比较基线快照。</summary>
    /// <param name="snapshotId">快照标识。</param>
    void SelectBaseline(MemorySnapshotId snapshotId);

    /// <summary>设置比较候选快照。</summary>
    /// <param name="snapshotId">快照标识。</param>
    void SelectCandidate(MemorySnapshotId snapshotId);

    /// <summary>获取指定快照的共享分析句柄。</summary>
    /// <param name="snapshotId">快照标识。</param>
    /// <returns>绑定到指定快照的分析对象。</returns>
    ISnapshotAnalysis GetAnalysis(MemorySnapshotId snapshotId);
}

