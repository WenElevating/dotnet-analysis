namespace DotnetAnalysis.Core.Diagnostics;

public sealed record MemorySnapshot
{
    public MemorySnapshot(
        MemorySnapshotId id,
        MemorySnapshotOrigin origin,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset? captureStartedAtUtc,
        DateTimeOffset? capturedAtUtc,
        MemorySnapshotState state)
    {
        Id = id;
        Origin = origin;
        RequestedAtUtc = requestedAtUtc;
        CaptureStartedAtUtc = captureStartedAtUtc;
        CapturedAtUtc = capturedAtUtc;
        State = state;
    }

    public MemorySnapshotId Id { get; }

    public MemorySnapshotOrigin Origin { get; }

    public DateTimeOffset RequestedAtUtc { get; }

    public DateTimeOffset? CaptureStartedAtUtc { get; }

    public DateTimeOffset? CapturedAtUtc { get; }

    public MemorySnapshotState State { get; private init; }

    internal MemorySnapshot MoveTo(MemorySnapshotState nextState)
    {
        if (!MemorySnapshotTransitionRules.CanMove(State, nextState))
        {
            throw new InvalidOperationException($"Cannot move memory snapshot from {State} to {nextState}.");
        }

        return this with { State = nextState };
    }
}
