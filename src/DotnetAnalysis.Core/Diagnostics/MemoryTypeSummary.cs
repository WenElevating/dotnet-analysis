namespace DotnetAnalysis.Core.Diagnostics;

public sealed record MemoryTypeSummary
{
    public MemoryTypeSummary(TypeIdentity type, long objectCount, long totalSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (objectCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(objectCount), objectCount, "Object count cannot be negative.");
        }

        if (totalSizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalSizeBytes), totalSizeBytes, "Total size cannot be negative.");
        }

        Type = type;
        ObjectCount = objectCount;
        TotalSizeBytes = totalSizeBytes;
    }

    public TypeIdentity Type { get; }

    public long ObjectCount { get; }

    public long TotalSizeBytes { get; }
}
