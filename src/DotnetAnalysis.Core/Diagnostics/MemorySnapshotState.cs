namespace DotnetAnalysis.Core.Diagnostics;

public enum MemorySnapshotState
{
    Pending,
    Capturing,
    Analyzing,
    Ready,
    Failed,
    Canceled
}
