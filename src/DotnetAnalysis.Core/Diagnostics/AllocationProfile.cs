namespace DotnetAnalysis.Core.Diagnostics;

public sealed record AllocationProfile
{
    public AllocationProfile(
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc,
        IReadOnlyList<AllocationHotspot> hotspots,
        AllocationProfileDataQuality dataQuality)
    {
        ArgumentNullException.ThrowIfNull(hotspots);
        if (endedAtUtc < startedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(endedAtUtc), endedAtUtc, "End time cannot precede start time.");
        }

        var hotspotSnapshot = hotspots.ToArray();
        if (dataQuality is AllocationProfileDataQuality.NotAvailable && hotspotSnapshot.Length != 0)
        {
            throw new ArgumentException("Unavailable allocation profiles cannot contain hotspots.", nameof(hotspots));
        }

        StartedAtUtc = startedAtUtc;
        EndedAtUtc = endedAtUtc;
        Hotspots = hotspotSnapshot;
        DataQuality = dataQuality;
    }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset EndedAtUtc { get; }

    public IReadOnlyList<AllocationHotspot> Hotspots { get; }

    public AllocationProfileDataQuality DataQuality { get; }

    public static AllocationProfile NotAvailable(DateTimeOffset startedAtUtc, DateTimeOffset endedAtUtc) =>
        new(startedAtUtc, endedAtUtc, Array.Empty<AllocationHotspot>(), AllocationProfileDataQuality.NotAvailable);
}
