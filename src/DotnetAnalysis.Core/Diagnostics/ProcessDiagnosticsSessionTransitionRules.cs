namespace DotnetAnalysis.Core.Diagnostics;

public static class ProcessDiagnosticsSessionTransitionRules
{
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
