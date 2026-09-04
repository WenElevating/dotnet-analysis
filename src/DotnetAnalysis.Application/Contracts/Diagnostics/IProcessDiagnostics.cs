using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Application.Contracts.Diagnostics;

/// <summary>
/// 进程枚举、附着和快照导入的诊断门面契约。
/// </summary>
public interface IProcessDiagnostics
{
    /// <summary>
    /// 异步枚举当前可诊断进程。
    /// </summary>
    Task<IReadOnlyList<TargetProcess>> GetProcessesAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// 附着到指定进程并返回可采样会话。
    /// </summary>
    Task<IProcessDiagnosticsSession> AttachAsync(
        TargetProcess process,
        CancellationToken cancellationToken);

    /// <summary>
    /// 打开现有 .gcdump 文件并返回导入快照。
    /// </summary>
    Task<MemorySnapshot> OpenSnapshotAsync(
        string filePath,
        CancellationToken cancellationToken);
}
