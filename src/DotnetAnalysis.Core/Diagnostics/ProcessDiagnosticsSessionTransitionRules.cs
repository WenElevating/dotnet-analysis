namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 定义诊断会话生命周期允许的状态转移。
/// </summary>
public static class ProcessDiagnosticsSessionTransitionRules
{
    /// <summary>
    /// 判断会话能否从指定状态转移到目标状态。
    /// </summary>
    /// <param name="from">当前状态。</param>
    /// <param name="to">候选下一状态。</param>
    /// <returns>转移被允许时返回 <see langword="true"/>。</returns>
    public static bool CanMove(ProcessDiagnosticsSessionState from, ProcessDiagnosticsSessionState to) =>
        from switch
        {
            ProcessDiagnosticsSessionState.Attaching => to is ProcessDiagnosticsSessionState.Monitoring
                or ProcessDiagnosticsSessionState.Failed,
            ProcessDiagnosticsSessionState.Monitoring => to is ProcessDiagnosticsSessionState.Ending
                or ProcessDiagnosticsSessionState.Failed,
            ProcessDiagnosticsSessionState.Ending => to is ProcessDiagnosticsSessionState.Ended
                or ProcessDiagnosticsSessionState.Failed,
            _ => false
        };
}
