using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Orchestration.Models;

namespace DotnetAnalysis.Orchestration.Tests;

/// <summary>验证编排层稳定模型的身份、能力、质量和状态契约。</summary>
[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names describe the required contract behavior.")]
public sealed class ApplicationContractTests
{
    [TestMethod]
    public void TargetContext_PreservesProcessIdAndStartTime()
    {
        var startedAt = new DateTimeOffset(2026, 9, 16, 1, 2, 3, TimeSpan.Zero);
        var target = new TargetProcess(42, startedAt, "sample", null);
        var capabilities = CreateCapabilities();

        var context = new TargetContext(target, "net10.0", TargetArchitecture.X64, capabilities);

        Assert.AreEqual(42, context.ProcessId);
        Assert.AreEqual(startedAt, context.StartedAtUtc);
        Assert.AreSame(target, context.Target);
    }

    [TestMethod]
    public void ProcessFilter_HasNoUiDependencyAndValidatesPageSize()
    {
        var filter = new ProcessFilter(processName: " sample ", pageSize: 25);

        Assert.AreEqual("sample", filter.ProcessName);
        Assert.AreEqual(25, filter.PageSize);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ProcessFilter(pageSize: 0));
        Assert.IsFalse(typeof(ProcessFilter).Assembly.GetReferencedAssemblies().Any(static name => name.Name == "PresentationFramework"));
    }

    [TestMethod]
    public void Capabilities_AreIndependentlyAvailable()
    {
        var unavailable = new DiagnosticCapabilityAvailability(false, "Profiler occupied", DiagnosticsErrorCode.ProfilerAttachUnavailable);
        var capabilities = new DiagnosticCapabilities(
            new DiagnosticCapabilityAvailability(true), unavailable,
            new DiagnosticCapabilityAvailability(true), new DiagnosticCapabilityAvailability(true), unavailable,
            DateTimeOffset.UtcNow);

        Assert.IsTrue(capabilities.StandardSnapshot.IsAvailable);
        Assert.IsFalse(capabilities.RetentionSnapshot.IsAvailable);
        Assert.AreEqual(DiagnosticsErrorCode.ProfilerAttachUnavailable, capabilities.RetentionSnapshot.ErrorCode);
        Assert.IsTrue(capabilities.ExecutionSampling.IsAvailable);
    }

    [TestMethod]
    public void OperationState_PreservesFailureCodeStageAndRetryability()
    {
        var failure = new DiagnosticFailure(DiagnosticsErrorCode.TargetChanged, DiagnosticOperationStage.Attaching, false, "目标身份已变化");
        var operation = new DiagnosticOperationState(Guid.NewGuid(), Guid.NewGuid(), DiagnosticOperationStage.Attaching, DiagnosticOperationStatus.Failed, failure: failure);

        Assert.AreEqual(DiagnosticsErrorCode.TargetChanged, operation.Failure!.ErrorCode);
        Assert.AreEqual(DiagnosticOperationStage.Attaching, operation.Failure.Stage);
        Assert.IsFalse(operation.Failure.Retryable);
    }

    [TestMethod]
    public void ApplicationState_ContainsGenerationSessionSnapshotAndOperationIdentities()
    {
        var sessionId = ProcessDiagnosticsSessionId.New();
        var snapshotId = MemorySnapshotId.New();
        var snapshot = new MemorySnapshot(snapshotId, MemorySnapshotOrigin.Imported, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, MemorySnapshotState.Ready);
        var operation = new DiagnosticOperationState(Guid.NewGuid(), Guid.NewGuid(), DiagnosticOperationStage.Querying, DiagnosticOperationStatus.Running, sessionId, snapshotId);

        var state = new DiagnosticsApplicationState(operation.Generation, DiagnosticsApplicationLifecycle.SnapshotAnalysis, sessionId: sessionId, snapshot: snapshot, operation: operation);

        Assert.AreEqual(operation.Generation, state.Generation);
        Assert.AreEqual(sessionId, state.SessionId);
        Assert.AreEqual(snapshotId, state.Snapshot!.Id);
        Assert.AreEqual(operation.OperationId, state.Operation!.OperationId);
    }

    [TestMethod]
    public void QualitySummary_DistinguishesRequiredQualityLevels()
    {
        var qualities = Enum.GetValues<DiagnosticQuality>();

        CollectionAssert.IsSubsetOf(
            new[] { DiagnosticQuality.Complete, DiagnosticQuality.Partial, DiagnosticQuality.Unavailable, DiagnosticQuality.Failed },
            qualities);
    }

    [TestMethod]
    public void LaunchTarget_ValidatesExecutableAndTimeout()
    {
        var target = new LaunchTarget("sample.exe", startupTimeout: TimeSpan.FromSeconds(5));

        Assert.AreEqual("sample.exe", target.ExecutablePath);
        Assert.AreEqual(TimeSpan.FromSeconds(5), target.StartupTimeout);
        Assert.ThrowsExactly<ArgumentException>(() => new LaunchTarget(" "));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LaunchTarget("sample.exe", startupTimeout: TimeSpan.Zero));
    }

    private static DiagnosticCapabilities CreateCapabilities() => new(
        new DiagnosticCapabilityAvailability(true),
        new DiagnosticCapabilityAvailability(false),
        new DiagnosticCapabilityAvailability(true),
        new DiagnosticCapabilityAvailability(true),
        new DiagnosticCapabilityAvailability(true),
        DateTimeOffset.UtcNow);
}
