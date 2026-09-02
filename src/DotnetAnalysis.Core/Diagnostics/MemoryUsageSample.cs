namespace DotnetAnalysis.Core.Diagnostics;

public sealed record MemoryUsageSample
{
    public MemoryUsageSample(
        DateTimeOffset observedAtUtc,
        long? managedHeapBytes,
        long? processMemoryBytes,
        MemoryUsageSampleState state)
    {
        if (managedHeapBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(managedHeapBytes), managedHeapBytes, "Byte values cannot be negative.");
        }

        if (processMemoryBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processMemoryBytes), processMemoryBytes, "Byte values cannot be negative.");
        }

        switch (state)
        {
            case MemoryUsageSampleState.Measured when managedHeapBytes is null || processMemoryBytes is null:
                throw new ArgumentException("Measured samples require both byte values.", nameof(state));
            case MemoryUsageSampleState.Unavailable or MemoryUsageSampleState.SessionEnded
                when managedHeapBytes is not null || processMemoryBytes is not null:
                throw new ArgumentException("Unavailable and session-ended samples must not contain byte values.", nameof(state));
        }

        ObservedAtUtc = observedAtUtc;
        ManagedHeapBytes = managedHeapBytes;
        ProcessMemoryBytes = processMemoryBytes;
        State = state;
    }

    public DateTimeOffset ObservedAtUtc { get; }

    public long? ManagedHeapBytes { get; }

    public long? ProcessMemoryBytes { get; }

    public MemoryUsageSampleState State { get; }
}
