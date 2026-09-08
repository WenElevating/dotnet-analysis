using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Tests.Core.Diagnostics;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class DiagnosticsValueTests
{
    [TestMethod]
    public void ProcessDiagnosticsSessionId_New_ProducesDistinctNonEmptyValues()
    {
        var first = ProcessDiagnosticsSessionId.New();
        var second = ProcessDiagnosticsSessionId.New();

        Assert.AreNotEqual(first, second);
        Assert.AreNotEqual(Guid.Empty, first.Value);
        Assert.AreNotEqual(Guid.Empty, second.Value);
    }

    [TestMethod]
    public void DiagnosticsIds_RejectEmptyValues()
    {
        Assert.Throws<ArgumentException>(() =>
            new ProcessDiagnosticsSessionId(Guid.Empty));
        Assert.Throws<ArgumentException>(() =>
            new MemorySnapshotId(Guid.Empty));
    }

    [TestMethod]
    public void TargetProcess_RequiresPositiveProcessId()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TargetProcess(
                0,
                Instant("2026-09-02T10:00:00Z"),
                "worker",
                "C:\\worker.exe"));
    }

    [TestMethod]
    public void MemoryUsageSample_Unavailable_UsesNullValues()
    {
        var sample = new MemoryUsageSample(
            Instant("2026-09-02T10:00:00Z"),
            null,
            null,
            MemoryUsageSampleState.Unavailable);

        Assert.IsNull(sample.ManagedHeapBytes);
        Assert.IsNull(sample.ProcessMemoryBytes);
    }

    [TestMethod]
    public void MemoryUsageSample_Measured_RequiresBothValues()
    {
        Assert.Throws<ArgumentException>(() =>
            new MemoryUsageSample(
                Instant("2026-09-02T10:00:00Z"),
                1,
                null,
                MemoryUsageSampleState.Measured));
    }

    [TestMethod]
    public void MemoryUsageSample_RejectsNegativeValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MemoryUsageSample(
                Instant("2026-09-02T10:00:00Z"),
                -1,
                1,
                MemoryUsageSampleState.Measured));
    }

    [TestMethod]
    public void MemoryUsageSample_SessionEnded_RequiresNullValues()
    {
        Assert.Throws<ArgumentException>(() =>
            new MemoryUsageSample(
                Instant("2026-09-02T10:00:00Z"),
                1,
                1,
                MemoryUsageSampleState.SessionEnded));
    }

    [TestMethod]
    public void TypeIdentity_RejectsEmptyName()
    {
        Assert.Throws<ArgumentException>(() =>
            new TypeIdentity(string.Empty, "Assembly"));
    }

    [TestMethod]
    public void CallStackFrame_RejectsEmptyName()
    {
        Assert.Throws<ArgumentException>(() =>
            new CallStackFrame(string.Empty, "Module", 12));
    }

    [TestMethod]
    public void AllocationProfile_NotAvailable_HasNoHotspots()
    {
        var profile = AllocationProfile.NotAvailable(
            Instant("2026-09-02T10:00:00Z"),
            Instant("2026-09-02T10:01:00Z"));

        Assert.AreEqual(AllocationProfileDataQuality.NotAvailable, profile.DataQuality);
        Assert.IsEmpty(profile.Hotspots);
    }

    [TestMethod]
    public void AllocationProfile_NotAvailable_ReportsUnavailableCallStackQuality()
    {
        var profile = AllocationProfile.NotAvailable(
            Instant("2026-09-04T00:00:00Z"),
            Instant("2026-09-04T00:01:00Z"));

        var quality = typeof(AllocationProfile).GetProperty("CallStackQuality")?.GetValue(profile)?.ToString();

        Assert.AreEqual("NotAvailable", quality);
    }

    [TestMethod]
    public void ExecutionTimeRange_ConvertsTimesToUtc()
    {
        var range = new ExecutionTimeRange(
            new DateTimeOffset(2026, 9, 2, 18, 0, 0, TimeSpan.FromHours(8)),
            new DateTimeOffset(2026, 9, 2, 18, 1, 0, TimeSpan.FromHours(8)));

        Assert.AreEqual(TimeSpan.Zero, range.StartAtUtc.Offset);
        Assert.AreEqual(TimeSpan.Zero, range.EndAtUtc.Offset);
        Assert.AreEqual(Instant("2026-09-02T10:00:00Z"), range.StartAtUtc);
        Assert.AreEqual(Instant("2026-09-02T10:01:00Z"), range.EndAtUtc);
    }

    [TestMethod]
    public void ExecutionTimeRange_WhenEndIsNotAfterStart_Throws()
    {
        var instant = DateTimeOffset.UnixEpoch;

        Assert.Throws<ArgumentOutOfRangeException>(() => new ExecutionTimeRange(instant, instant));
    }

    [TestMethod]
    public void ExecutionTimeRange_WhenEndIsEarlierThanStart_Throws()
    {
        var instant = DateTimeOffset.UnixEpoch;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ExecutionTimeRange(instant.AddMinutes(1), instant));
    }

    [TestMethod]
    public void SourceLocation_NormalizesPathAndRejectsInvalidValues()
    {
        var location = new SourceLocation(".\\source.cs", 12, 3);

        Assert.AreEqual(Path.GetFullPath(".\\source.cs"), location.FilePath);
        Assert.Throws<ArgumentException>(() => new SourceLocation(" ", 1, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceLocation("source.cs", 0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceLocation("source.cs", 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceLocation("source.cs", -1, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceLocation("source.cs", 1, -1));
    }

    [TestMethod]
    public void ExecutionFrame_RejectsEmptyMethodName()
    {
        Assert.Throws<ArgumentException>(() => new ExecutionFrame(" ", null, null));
    }

    [TestMethod]
    public void ExecutionHotspot_RejectsNegativeSampleCountsAndAllowsZero()
    {
        var frame = CreateExecutionFrame();

        Assert.Throws<ArgumentOutOfRangeException>(() => new ExecutionHotspot(frame, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExecutionHotspot(frame, 0, -1));

        var hotspot = new ExecutionHotspot(frame, 0, 0);
        Assert.AreEqual(0L, hotspot.InclusiveSampleCount);
        Assert.AreEqual(0L, hotspot.ExclusiveSampleCount);
    }

    [TestMethod]
    public void ExecutionCallTreeNode_RejectsNegativeSampleCountsAndExposesReadOnlyChildren()
    {
        var frame = CreateExecutionFrame();
        var children = new List<ExecutionCallTreeNode>();

        Assert.Throws<ArgumentOutOfRangeException>(() => new ExecutionCallTreeNode(frame, -1, 0, children));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExecutionCallTreeNode(frame, 0, -1, children));

        var child = new ExecutionCallTreeNode(frame, 0, 0, Array.Empty<ExecutionCallTreeNode>());
        children.Add(child);
        var node = new ExecutionCallTreeNode(frame, 0, 0, children);
        children.Clear();

        Assert.HasCount(1, node.Children);
        Assert.IsFalse(node.Children is ExecutionCallTreeNode[]);
        var exposedChildren = (IList<ExecutionCallTreeNode>)node.Children;
        Assert.Throws<NotSupportedException>(() => exposedChildren[0] = CreateExecutionCallTreeNode());
        Assert.AreSame(child, node.Children[0]);
    }

    [TestMethod]
    public void ExecutionProfile_ExposesReadOnlyCollectionSnapshotsAndAllowsEmptySamples()
    {
        var hotspot = new ExecutionHotspot(CreateExecutionFrame(), 0, 0);
        var root = CreateExecutionCallTreeNode();
        var hotspots = new List<ExecutionHotspot> { hotspot };
        var roots = new List<ExecutionCallTreeNode> { root };
        var profile = new ExecutionProfile(
            new ExecutionTimeRange(Instant("2026-09-02T10:00:00Z"), Instant("2026-09-02T10:01:00Z")),
            0,
            0,
            hotspots,
            roots);

        hotspots.Clear();
        roots.Clear();

        Assert.HasCount(1, profile.Hotspots);
        Assert.HasCount(1, profile.CallTreeRoots);
        Assert.IsFalse(profile.Hotspots is ExecutionHotspot[]);
        Assert.IsFalse(profile.CallTreeRoots is ExecutionCallTreeNode[]);
        var exposedHotspots = (IList<ExecutionHotspot>)profile.Hotspots;
        var exposedRoots = (IList<ExecutionCallTreeNode>)profile.CallTreeRoots;
        Assert.Throws<NotSupportedException>(() => exposedHotspots[0] = new ExecutionHotspot(CreateExecutionFrame(), 0, 0));
        Assert.Throws<NotSupportedException>(() => exposedRoots[0] = CreateExecutionCallTreeNode());
        Assert.AreSame(hotspot, profile.Hotspots[0]);
        Assert.AreSame(root, profile.CallTreeRoots[0]);

        var emptyProfile = new ExecutionProfile(
            new ExecutionTimeRange(Instant("2026-09-02T10:00:00Z"), Instant("2026-09-02T10:01:00Z")),
            0,
            0,
            Array.Empty<ExecutionHotspot>(),
            Array.Empty<ExecutionCallTreeNode>());

        Assert.IsEmpty(emptyProfile.Hotspots);
        Assert.IsEmpty(emptyProfile.CallTreeRoots);
    }

    [TestMethod]
    public void ExecutionProfile_RejectsNegativeCounts()
    {
        var range = new ExecutionTimeRange(Instant("2026-09-02T10:00:00Z"), Instant("2026-09-02T10:01:00Z"));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ExecutionProfile(range, -1, 0, Array.Empty<ExecutionHotspot>(), Array.Empty<ExecutionCallTreeNode>()));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ExecutionProfile(range, 0, -1, Array.Empty<ExecutionHotspot>(), Array.Empty<ExecutionCallTreeNode>()));
    }

    private static ExecutionFrame CreateExecutionFrame() =>
        new("Worker.Run", "Worker", new SourceLocation("source.cs", 1, null));

    private static ExecutionCallTreeNode CreateExecutionCallTreeNode() =>
        new(CreateExecutionFrame(), 0, 0, Array.Empty<ExecutionCallTreeNode>());

    private static DateTimeOffset Instant(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
