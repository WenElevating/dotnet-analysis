using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Orchestration;
using DotnetAnalysis.Orchestration.Models;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

/// <summary>
/// 验证编排入口在真实目标身份变化、目标退出、取消和 Retention 能力门禁下的稳定错误边界。
/// </summary>
[TestClass]
[DoNotParallelize]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "测试名描述真实失败流程。")]
public sealed class OrchestrationFailureWorkflowTests
{
    /// <summary>
    /// 验证附着前启动时间不匹配时返回 PID 复用保护错误，而不是附着到当前 PID 实例。
    /// </summary>
    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AttachAsync_WithStaleStartTimeReportsTargetChanged()
    {
        await using var fixture = OrchestratedIntegrationFixture.Create();
        await using var target = await IntegrationTestHost.StartTargetAsync("net10.0");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var current = (await fixture.Application.FindTargetsAsync(new ProcessFilter(pageSize: 1_000), timeout.Token))
            .Single(candidate => candidate.ProcessId == target.ProcessId);
        var stale = new TargetProcess(
            current.ProcessId,
            current.StartedAtUtc.AddSeconds(-1),
            current.ProcessName,
            current.ExecutablePath);

        var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
            () => fixture.Application.AttachAsync(stale, timeout.Token));

        Assert.AreEqual(DiagnosticsErrorCode.TargetChanged, exception.ErrorCode);
        Assert.AreEqual(DiagnosticsApplicationPhase.Failed, fixture.Application.State.Phase);
    }

    /// <summary>
    /// 验证目标在活动会话期间退出后，后续真实捕获操作返回诊断错误而不是伪造快照。
    /// </summary>
    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AttachedSession_WhenTargetExitsReportsStableDiagnosticsError()
    {
        await using var fixture = OrchestratedIntegrationFixture.Create();
        await using var target = await IntegrationTestHost.StartTargetAsync("net10.0");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var current = (await fixture.Application.FindTargetsAsync(new ProcessFilter(pageSize: 1_000), timeout.Token))
            .Single(candidate => candidate.ProcessId == target.ProcessId);
        var session = await fixture.Application.AttachAsync(current, timeout.Token);

        await IntegrationTestHost.TerminateTargetAsync(target.ProcessId);
        var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
            () => session.CaptureAsync(MemorySnapshotCaptureMode.Standard, timeout.Token));

        Assert.IsTrue(
            exception.ErrorCode is DiagnosticsErrorCode.TargetExited
                or DiagnosticsErrorCode.TargetChanged
                or DiagnosticsErrorCode.CaptureFailed,
            $"Unexpected target-exit error code: {exception.ErrorCode}.");
        Assert.IsNull(fixture.Application.ActiveSession);
    }

    /// <summary>
    /// 验证调用方取消不会产生正式快照，并映射为稳定的捕获取消错误。
    /// </summary>
    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task CaptureAsync_WithPreCancelledTokenReportsCaptureCancelled()
    {
        await using var fixture = OrchestratedIntegrationFixture.Create();
        await using var target = await IntegrationTestHost.StartTargetAsync("net10.0");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var current = (await fixture.Application.FindTargetsAsync(new ProcessFilter(pageSize: 1_000), timeout.Token))
            .Single(candidate => candidate.ProcessId == target.ProcessId);
        var session = await fixture.Application.AttachAsync(current, timeout.Token);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
            () => session.CaptureAsync(MemorySnapshotCaptureMode.Standard, cancellation.Token));

        Assert.AreEqual(DiagnosticsErrorCode.CaptureCancelled, exception.ErrorCode);
        Assert.IsEmpty(fixture.Application.Snapshots.Snapshots);
    }

    /// <summary>
    /// 验证当前真实能力探测明确拒绝 Retention 捕获，不把标准快照伪装成保留分析结果。
    /// </summary>
    /// <param name="targetFramework">受测目标运行时框架。</param>
    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    [DataRow("net8.0")]
    [DataRow("net9.0")]
    [DataRow("net10.0")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task CaptureAsync_RetentionAnalysisHonorsCapabilityGate(string targetFramework)
    {
        if (!IntegrationTestHost.GetSupportedTargetFrameworks().Contains(targetFramework, StringComparer.Ordinal))
        {
            Assert.Inconclusive($"The {targetFramework} runtime is not installed on this machine.");
        }

        await using var fixture = OrchestratedIntegrationFixture.Create();
        await using var target = await IntegrationTestHost.StartTargetAsync(targetFramework, initialObjectCount: 256);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var current = (await fixture.Application.FindTargetsAsync(new ProcessFilter(pageSize: 1_000), timeout.Token))
            .Single(candidate => candidate.ProcessId == target.ProcessId);
        var session = await fixture.Application.AttachAsync(current, timeout.Token);

        Assert.IsFalse(session.Capabilities.RetentionSnapshot.IsAvailable);
        var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
            () => session.CaptureAsync(MemorySnapshotCaptureMode.RetentionAnalysis, timeout.Token));

        Assert.AreEqual(DiagnosticsErrorCode.ProfilerAttachUnavailable, exception.ErrorCode);
        Assert.IsEmpty(fixture.Application.Snapshots.Snapshots);
    }
}
