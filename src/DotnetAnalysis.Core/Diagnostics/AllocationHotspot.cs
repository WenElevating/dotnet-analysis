namespace DotnetAnalysis.Core.Diagnostics;

public sealed record AllocationHotspot
{
    public AllocationHotspot(
        TypeIdentity type,
        long observedAllocatedBytes,
        IReadOnlyList<CallStackFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(frames);
        if (observedAllocatedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedAllocatedBytes),
                observedAllocatedBytes,
                "Observed allocated bytes cannot be negative.");
        }

        Type = type;
        ObservedAllocatedBytes = observedAllocatedBytes;
        Frames = frames.ToArray();
    }

    public TypeIdentity Type { get; }

    public long ObservedAllocatedBytes { get; }

    public IReadOnlyList<CallStackFrame> Frames { get; }
}
