using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Tests.Core.Diagnostics;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class DiagnosticsStateMachineTests
{
    [TestMethod]
    public void SnapshotRules_AllowCaptureToAnalyzeAndBecomeReady()
    {
        Assert.IsTrue(MemorySnapshotTransitionRules.CanMove(
            MemorySnapshotState.Capturing,
            MemorySnapshotState.Analyzing));
        Assert.IsTrue(MemorySnapshotTransitionRules.CanMove(
            MemorySnapshotState.Analyzing,
            MemorySnapshotState.Ready));
        Assert.IsFalse(MemorySnapshotTransitionRules.CanMove(
            MemorySnapshotState.Failed,
            MemorySnapshotState.Capturing));
    }

    [TestMethod]
    public void SessionRules_AllowAttachingToMonitoringAndEnding()
    {
        Assert.IsTrue(ProcessDiagnosticsSessionTransitionRules.CanMove(
            ProcessDiagnosticsSessionState.Attaching,
            ProcessDiagnosticsSessionState.Monitoring));
        Assert.IsTrue(ProcessDiagnosticsSessionTransitionRules.CanMove(
            ProcessDiagnosticsSessionState.Monitoring,
            ProcessDiagnosticsSessionState.Ending));
        Assert.IsFalse(ProcessDiagnosticsSessionTransitionRules.CanMove(
            ProcessDiagnosticsSessionState.Failed,
            ProcessDiagnosticsSessionState.Attaching));
    }

    [TestMethod]
    public void MemorySnapshot_StoresProvidedAnalysisState()
    {
        var snapshot = new MemorySnapshot(
            MemorySnapshotId.New(),
            MemorySnapshotOrigin.Imported,
            Instant("2026-09-02T10:00:00Z"),
            null,
            Instant("2026-09-02T10:01:00Z"),
            MemorySnapshotState.Ready);

        Assert.AreEqual(MemorySnapshotState.Ready, snapshot.State);
    }

    [TestMethod]
    public void MemorySnapshotAnalysis_HoldsSnapshotTypesAndProfile()
    {
        var snapshot = new MemorySnapshot(
            MemorySnapshotId.New(),
            MemorySnapshotOrigin.Imported,
            Instant("2026-09-02T10:00:00Z"),
            null,
            Instant("2026-09-02T10:01:00Z"),
            MemorySnapshotState.Ready);
        var analysis = new MemorySnapshotAnalysis(
            snapshot,
            Array.Empty<MemoryTypeSummary>(),
            AllocationProfile.NotAvailable(
                Instant("2026-09-02T10:00:00Z"),
                Instant("2026-09-02T10:01:00Z")));

        Assert.AreEqual(snapshot, analysis.Snapshot);
        Assert.IsEmpty(analysis.Types);
    }

    private static DateTimeOffset Instant(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
