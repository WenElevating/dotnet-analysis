namespace DotnetAnalysis.Application.Contracts.Diagnostics;

/// <summary>
/// 定义宿主无关的目标进程启动边界。
/// </summary>
public interface ITargetProcessLauncher
{
    /// <summary>
    /// 启动目标程序并等待其身份可被读取。
    /// </summary>
    /// <param name="request">目标程序启动请求。</param>
    /// <param name="cancellationToken">取消等待的令牌。</param>
    /// <returns>包含 PID、启动时间和身份验证状态的启动结果。</returns>
    Task<TargetProcessLaunchResult> LaunchAsync(
        TargetProcessLaunchRequest request,
        CancellationToken cancellationToken);
}
