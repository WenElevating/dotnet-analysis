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
    /// <summary>验证目标上下文保留 PID 和启动时间。</summary>
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

    /// <summary>验证筛选模型不依赖 UI 且校验页大小。</summary>
    [TestMethod]
    public void ProcessFilter_HasNoUiDependencyAndValidatesPageSize()
    {
        var filter = new ProcessFilter(processName: " sample ", pageSize: 25);

        Assert.AreEqual("sample", filter.ProcessName);
        Assert.AreEqual(25, filter.PageSize);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ProcessFilter(pageSize: 0));
        Assert.IsFalse(typeof(ProcessFilter).Assembly.GetReferencedAssemblies().Any(static name => name.Name == "PresentationFramework"));
    }

    /// <summary>验证每项诊断能力可以独立报告可用性。</summary>
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

    /// <summary>验证操作失败保留错误码、阶段和重试标记。</summary>
    [TestMethod]
    public void OperationState_PreservesFailureCodeStageAndRetryability()
    {
        var failure = new DiagnosticFailure(DiagnosticsErrorCode.TargetChanged, DiagnosticOperationStage.Querying, false, "目标身份已变化");
        var operation = new DiagnosticOperationState(Guid.NewGuid(), Guid.NewGuid(), DiagnosticOperationStage.Querying, DiagnosticOperationStatus.Failed, failure: failure);

        Assert.AreEqual(DiagnosticsErrorCode.TargetChanged, operation.Failure!.ErrorCode);
        Assert.AreEqual(DiagnosticOperationStage.Querying, operation.Failure.Stage);
        Assert.IsFalse(operation.Failure.Retryable);
    }

    /// <summary>验证状态快照保留并校验 generation、session、snapshot 和 operation 身份。</summary>
    [TestMethod]
    public void ApplicationState_ContainsGenerationSessionSnapshotAndOperationIdentities()
    {
        var sessionId = ProcessDiagnosticsSessionId.New();
        var snapshotId = MemorySnapshotId.New();
        var snapshot = new MemorySnapshot(snapshotId, MemorySnapshotOrigin.Imported, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, MemorySnapshotState.Ready);
        var operation = new DiagnosticOperationState(Guid.NewGuid(), Guid.NewGuid(), DiagnosticOperationStage.Querying, DiagnosticOperationStatus.Running, sessionId, snapshotId, MemorySnapshotCaptureMode.Standard);

        var state = new DiagnosticsApplicationState(operation.Generation, DiagnosticsApplicationPhase.SnapshotSelection, ProcessDiagnosticsSessionState.Monitoring, sessionId: sessionId, snapshot: snapshot, operation: operation);

        Assert.AreEqual(operation.Generation, state.Generation);
        Assert.AreEqual(sessionId, state.SessionId);
        Assert.AreEqual(snapshotId, state.Snapshot!.Id);
        Assert.AreEqual(operation.OperationId, state.Operation!.OperationId);
        Assert.AreEqual(MemorySnapshotCaptureMode.Standard, state.Operation.CaptureMode);
        Assert.ThrowsExactly<ArgumentException>(() => new DiagnosticsApplicationState(Guid.NewGuid(), DiagnosticsApplicationPhase.SnapshotSelection, operation: operation));
        Assert.ThrowsExactly<ArgumentException>(() => new DiagnosticsApplicationState(operation.Generation, DiagnosticsApplicationPhase.SnapshotSelection, sessionId: ProcessDiagnosticsSessionId.New(), operation: operation));
    }

    /// <summary>验证质量摘要区分完整、部分、不可用和失败。</summary>
    [TestMethod]
    public void QualitySummary_DistinguishesRequiredQualityLevels()
    {
        var qualities = Enum.GetValues<DiagnosticQuality>();

        CollectionAssert.IsSubsetOf(
            new[] { DiagnosticQuality.Complete, DiagnosticQuality.Partial, DiagnosticQuality.Unavailable, DiagnosticQuality.Failed },
            qualities);
    }

    /// <summary>验证启动目标校验可执行文件路径和超时。</summary>
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
