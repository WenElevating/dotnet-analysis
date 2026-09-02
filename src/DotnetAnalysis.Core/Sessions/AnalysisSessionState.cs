namespace DotnetAnalysis.Core.Sessions;

public enum AnalysisSessionState
{
    Created,
    Preflighting,
    CapturingAllocations,
    FinishingTrace,
    CapturingHeapSnapshot,
    Analyzing,
    Completed,
    Canceling,
    Canceled,
    Failed
}
