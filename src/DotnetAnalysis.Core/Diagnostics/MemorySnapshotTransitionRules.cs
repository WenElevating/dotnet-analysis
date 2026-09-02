namespace DotnetAnalysis.Core.Diagnostics;

public static class MemorySnapshotTransitionRules
{
    public static bool CanMove(MemorySnapshotState from, MemorySnapshotState to) =>
        from switch
        {
            MemorySnapshotState.Pending => to is MemorySnapshotState.Capturing
                or MemorySnapshotState.Canceled
                or MemorySnapshotState.Failed,
            MemorySnapshotState.Capturing => to is MemorySnapshotState.Analyzing
                or MemorySnapshotState.Canceled
                or MemorySnapshotState.Failed,
            MemorySnapshotState.Analyzing => to is MemorySnapshotState.Ready
                or MemorySnapshotState.Canceled
                or MemorySnapshotState.Failed,
            MemorySnapshotState.Failed => to is MemorySnapshotState.Analyzing
                or MemorySnapshotState.Canceled,
            _ => false
        };
}
