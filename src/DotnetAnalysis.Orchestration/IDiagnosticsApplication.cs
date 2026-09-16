using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Orchestration.Models;

namespace DotnetAnalysis.Orchestration;

/// <summary>
/// 提供宿主无关的诊断应用上下文；同一实例同时只拥有一个活动目标会话。
/// </summary>
public interface IDiagnosticsApplication : IAsyncDisposable
{
    /// <summary>当前应用上下文代次；切换目标或打开快照时会变化。</summary>
    Guid Generation { get; }

    /// <summary>当前不可变应用状态快照。</summary>
    DiagnosticsApplicationState State { get; }

    /// <summary>当前活动目标会话；没有活动会话时为空。</summary>
    IAnalysisSession? ActiveSession { get; }

    /// <summary>当前上下文拥有的快照集合。</summary>
    ISnapshotCollection Snapshots { get; }

    /// <summary>查找符合筛选条件的目标进程。</summary>
    /// <param name="filter">目标筛选、排序和分页条件。</param>
    /// <param name="cancellationToken">取消查找的令牌。</param>
    /// <returns>按稳定目标身份去重后的候选集合。</returns>
    Task<IReadOnlyList<TargetProcess>> FindTargetsAsync(ProcessFilter filter, CancellationToken cancellationToken);

    /// <summary>探测并附着指定目标，替换当前活动会话。</summary>
    /// <param name="target">需要附着的目标身份。</param>
    /// <param name="cancellationToken">取消探测或附着的令牌。</param>
    /// <returns>新代次拥有的活动分析会话。</returns>
    Task<IAnalysisSession> AttachAsync(TargetProcess target, CancellationToken cancellationToken);

    /// <summary>启动目标程序、验证身份、探测能力并自动附着。</summary>
    /// <param name="target">启动参数和失败处理策略。</param>
    /// <param name="cancellationToken">取消启动等待或附着的令牌。</param>
    /// <returns>新代次拥有的活动分析会话。</returns>
    Task<IAnalysisSession> LaunchAndAttachAsync(LaunchTarget target, CancellationToken cancellationToken);

    /// <summary>打开正式快照并将其加入当前快照集合。</summary>
    /// <param name="filePath">待打开的快照路径。</param>
    /// <param name="cancellationToken">取消打开或分析准备的令牌。</param>
    /// <returns>绑定到已导入快照的分析句柄。</returns>
    Task<ISnapshotAnalysis> OpenSnapshotAsync(string filePath, CancellationToken cancellationToken);

    /// <summary>停止活动会话并关闭应用上下文；重复调用不会重复释放资源。</summary>
    /// <param name="cancellationToken">取消等待旧会话收尾的令牌。</param>
    Task CloseAsync(CancellationToken cancellationToken);
}