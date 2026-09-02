namespace DotnetAnalysis.Core.Diagnostics;

public sealed record MemoryReferencePath
{
    public MemoryReferencePath(ulong targetObjectAddress, IReadOnlyList<MemoryObjectInfo> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);

        TargetObjectAddress = targetObjectAddress;
        Objects = objects.ToArray();
    }

    public ulong TargetObjectAddress { get; }

    public IReadOnlyList<MemoryObjectInfo> Objects { get; }
}
