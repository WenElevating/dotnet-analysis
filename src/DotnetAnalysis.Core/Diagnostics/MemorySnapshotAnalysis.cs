namespace DotnetAnalysis.Core.Diagnostics;

public sealed record MemorySnapshotAnalysis
{
    public MemorySnapshotAnalysis(
        MemorySnapshot snapshot,
        IReadOnlyList<MemoryTypeSummary> types,
        AllocationProfile allocationProfile)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(allocationProfile);

        Snapshot = snapshot;
        Types = types.ToArray();
        AllocationProfile = allocationProfile;
    }

    public MemorySnapshot Snapshot { get; }

    public IReadOnlyList<MemoryTypeSummary> Types { get; }

    public AllocationProfile AllocationProfile { get; }
}
