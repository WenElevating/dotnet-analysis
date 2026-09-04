using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Application.Snapshots;

/// <summary>
/// 未配置具体读取器时使用的快照分析占位实现。
/// </summary>
public sealed class MemorySnapshotAnalysisService : IMemorySnapshotAnalysisService
{
    /// <summary>
    /// 报告未注册快照读取器的占位分析服务。
    /// </summary>
    /// <param name="snapshot">待分析快照。</param>
    /// <param name="cancellationToken">调用前检查的取消令牌。</param>
    public Task<MemorySnapshotAnalysis> AnalyzeAsync(
        MemorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        throw new DiagnosticsException(
            DiagnosticsErrorCode.SnapshotFormatNotSupported,
            "No memory snapshot reader has been registered.");
    }

    /// <summary>
    /// 报告未注册快照读取器。
    /// </summary>
    public Task<IReadOnlyList<MemoryObjectInfo>> GetObjectsAsync(
        MemorySnapshot snapshot,
        TypeIdentity type,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(type);
        cancellationToken.ThrowIfCancellationRequested();
        throw new DiagnosticsException(
            DiagnosticsErrorCode.SnapshotFormatNotSupported,
            "No memory snapshot reader has been registered.");
    }

    /// <summary>
    /// 报告未注册快照读取器。
    /// </summary>
    public Task<MemoryReferencePath?> GetReferencePathAsync(
        MemorySnapshot snapshot,
        ulong objectAddress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        throw new DiagnosticsException(
            DiagnosticsErrorCode.SnapshotFormatNotSupported,
            "No memory snapshot reader has been registered.");
    }
}
