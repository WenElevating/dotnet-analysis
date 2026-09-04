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

    private static DateTimeOffset Instant(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
