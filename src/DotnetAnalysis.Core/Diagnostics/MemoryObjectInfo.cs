namespace DotnetAnalysis.Core.Diagnostics;

public sealed record MemoryObjectInfo
{
    public MemoryObjectInfo(ulong address, TypeIdentity type, long sizeBytes)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (sizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), sizeBytes, "Object size cannot be negative.");
        }

        Address = address;
        Type = type;
        SizeBytes = sizeBytes;
    }

    public ulong Address { get; }

    public TypeIdentity Type { get; }

    public long SizeBytes { get; }
}
