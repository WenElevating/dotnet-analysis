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
    /// 探测目标进程的运行时和诊断能力，不创建活动诊断会话。
    /// </summary>
    /// <param name="process">需要探测的目标进程身份。</param>
    /// <param name="cancellationToken">取消本次探测的令牌。</param>
    /// <returns>由 Diagnostics 层验证并记录的能力证据。</returns>
    /// <exception cref="DiagnosticsException">目标身份变化或能力探测失败时引发。</exception>
    Task<TargetProcessCapabilities> ProbeCapabilitiesAsync(
        TargetProcess process,
        CancellationToken cancellationToken) => Task.FromException<TargetProcessCapabilities>(
            new DiagnosticsException(
                DiagnosticsErrorCode.RuntimeNotSupported,
                "当前诊断适配器不支持独立能力探测。"));

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
