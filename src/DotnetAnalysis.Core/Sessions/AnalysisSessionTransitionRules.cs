namespace DotnetAnalysis.Core.Sessions;

public static class AnalysisSessionTransitionRules
{
    public static bool CanMove(AnalysisSessionState from, AnalysisSessionState to)
    {
        return from switch
        {
            AnalysisSessionState.Created => to is AnalysisSessionState.Preflighting
                or AnalysisSessionState.Canceling
                or AnalysisSessionState.Failed,
            AnalysisSessionState.Preflighting => to is AnalysisSessionState.CapturingAllocations
                or AnalysisSessionState.Canceling
                or AnalysisSessionState.Failed,
            AnalysisSessionState.CapturingAllocations => to is AnalysisSessionState.FinishingTrace
                or AnalysisSessionState.Canceling
                or AnalysisSessionState.Failed,
            AnalysisSessionState.FinishingTrace => to is AnalysisSessionState.CapturingHeapSnapshot
                or AnalysisSessionState.Canceling
                or AnalysisSessionState.Failed,
            AnalysisSessionState.CapturingHeapSnapshot => to is AnalysisSessionState.Analyzing
                or AnalysisSessionState.Canceling
                or AnalysisSessionState.Failed,
            AnalysisSessionState.Analyzing => to is AnalysisSessionState.Completed
                or AnalysisSessionState.Canceling
                or AnalysisSessionState.Failed,
            AnalysisSessionState.Canceling => to is AnalysisSessionState.Canceled
                or AnalysisSessionState.Failed,
            _ => false
        };
    }
}
