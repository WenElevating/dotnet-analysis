namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 诊断会话从附着到释放的生命周期状态。
/// </summary>
public enum ProcessDiagnosticsSessionState
{
    /// <summary>
    /// 正在验证并附着目标进程。
    /// </summary>
    Attaching,
    /// <summary>
    /// 已附着并持续采样。
    /// </summary>
    Monitoring,
    /// <summary>
    /// 正在结束会话和释放资源。
    /// </summary>
    Ending,
    /// <summary>
    /// 会话已正常结束。
    /// </summary>
    Ended,
    /// <summary>
    /// 附着或运行过程中发生无法恢复的失败。
    /// </summary>
    Failed
}
