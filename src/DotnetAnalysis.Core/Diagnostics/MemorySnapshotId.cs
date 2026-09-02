namespace DotnetAnalysis.Core.Diagnostics;

public readonly record struct MemorySnapshotId
{
    public MemorySnapshotId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Snapshot ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static MemorySnapshotId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}
