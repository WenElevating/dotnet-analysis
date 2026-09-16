using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Orchestration;
using DotnetAnalysis.Orchestration.Models;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

/// <summary>
/// 验证 Task 7 应用入口驱动真实 .NET 8、9、10 目标完成完整内存诊断流程。
/// </summary>
[TestClass]
[DoNotParallelize]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "测试名描述真实业务流程。")]
public sealed class OrchestrationWorkflowIntegrationTests
{
    /// <summary>
    /// 验证发现、能力探测、附着、样本、三次标准快照、类型分页、引用路径、比较、停止和重开流程。
    /// </summary>
    /// <param name="targetFramework">受测目标运行时框架。</param>
    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    [DataRow("net8.0")]
    [DataRow("net9.0")]
    [DataRow("net10.0")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task RealTarget_CompletesThreeSnapshotAnalysisWorkflow(string targetFramework)
    {
        if (!IntegrationTestHost.GetSupportedTargetFrameworks().Contains(targetFramework, StringComparer.Ordinal))
        {
            Assert.Inconclusive($"The {targetFramework} runtime is not installed on this machine.");
        }

        await using var fixture = OrchestratedIntegrationFixture.Create();
        await using var target = await IntegrationTestHost.StartTargetAsync(targetFramework, initialObjectCount: 512);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var cancellationToken = timeout.Token;

        var candidates = await fixture.Application.FindTargetsAsync(new ProcessFilter(pageSize: 1_000), cancellationToken);
        var targetProcess = candidates.Single(candidate => candidate.ProcessId == target.ProcessId);
        var session = await fixture.Application.AttachAsync(targetProcess, cancellationToken);

        Assert.AreSame(session, fixture.Application.ActiveSession);
        Assert.AreEqual(ProcessDiagnosticsSessionState.Monitoring, session.State);
        Assert.IsTrue(session.Capabilities.StandardSnapshot.IsAvailable);
        _ = await WaitForMeasuredSampleAsync(session, cancellationToken);

        var snapshots = new List<MemorySnapshot>();
        ISnapshotAnalysis? firstAnalysis = null;
        ISnapshotAnalysis? lastAnalysis = null;
        for (var index = 0; index < 3; index++)
        {
            var snapshot = await fixture.Application.Snapshots.AddCapturedAsync(
                token => session.CaptureAsync(MemorySnapshotCaptureMode.Standard, token),
                cancellationToken);
            snapshots.Add(snapshot);

            var analysisHandle = fixture.Application.Snapshots.GetAnalysis(snapshot.Id);
            var analysis = await analysisHandle.AnalyzeAsync(cancellationToken);
            Assert.IsNotEmpty(analysis.Types);
            Assert.AreEqual(snapshot.Id, analysis.Snapshot.Id);
            if (index == 0)
            {
                firstAnalysis = analysisHandle;
            }

            lastAnalysis = analysisHandle;
        }

        Assert.HasCount(3, fixture.Application.Snapshots.Snapshots);
        Assert.IsNotNull(firstAnalysis);
        Assert.IsNotNull(lastAnalysis);

        var firstSummary = (await firstAnalysis!.AnalyzeAsync(cancellationToken))
            .Types
            .Where(summary => summary.ObjectCount > 0)
            .OrderByDescending(summary => summary.ObjectCount)
            .First();
        var page = await firstAnalysis.GetObjectsPageAsync(firstSummary.Type, 0, 8, cancellationToken);
        Assert.IsGreaterThan(0L, page.TotalObjectCount);
        Assert.IsNotEmpty(page.Objects);
        var referencePath = await firstAnalysis.GetReferencePathAsync(page.Objects[0].Address, cancellationToken);
        if (referencePath is not null)
        {
            Assert.AreEqual(page.Objects[0].Address, referencePath.TargetObjectAddress);
        }

        fixture.Application.Snapshots.SelectBaseline(snapshots[0].Id);
        fixture.Application.Snapshots.SelectCandidate(snapshots[2].Id);
        var comparison = firstAnalysis.CompareWith(lastAnalysis!);
        var comparisonResult = await comparison.AnalyzeAsync(cancellationToken);
        Assert.AreEqual(snapshots[0].Id, comparisonResult.BaselineSnapshotId);
        Assert.AreEqual(snapshots[2].Id, comparisonResult.CandidateSnapshotId);

        await session.StopAsync(cancellationToken);

        var reopenedPath = fixture.SnapshotLayout.GetFinalDumpPath(snapshots[2].Id);
        var reopenedAnalysis = await fixture.Application.OpenSnapshotAsync(reopenedPath, cancellationToken);
        var reopened = await reopenedAnalysis.AnalyzeAsync(cancellationToken);
        Assert.AreEqual(snapshots[2].Id, reopened.Snapshot.Id);
        Assert.IsNotEmpty(reopened.Types);
        Assert.HasCount(1, fixture.Application.Snapshots.Snapshots);

        await fixture.Application.CloseAsync(cancellationToken);
        Assert.IsNull(fixture.Application.ActiveSession);
        Assert.AreEqual(DiagnosticsApplicationPhase.Closed, fixture.Application.State.Phase);
    }

    /// <summary>
    /// 验证应用入口通过真实启动器启动目标、读取身份并自动附着。
    /// </summary>
    /// <param name="targetFramework">受测目标运行时框架。</param>
    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    [DataRow("net8.0")]
    [DataRow("net9.0")]
    [DataRow("net10.0")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task LaunchAndAttach_StartsAndAttachesRealTarget(string targetFramework)
    {
        if (!IntegrationTestHost.GetSupportedTargetFrameworks().Contains(targetFramework, StringComparer.Ordinal))
        {
            Assert.Inconclusive($"The {targetFramework} runtime is not installed on this machine.");
        }

        await using var fixture = OrchestratedIntegrationFixture.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var executablePath = IntegrationTestHost.ResolveTargetExecutablePath(targetFramework);
        var launchTarget = new LaunchTarget(
            executablePath,
            arguments: "--task8-startup",
            workingDirectory: Path.GetDirectoryName(executablePath),
            strategy: LaunchTargetStrategy.TerminateOnFailure,
            startupTimeout: TimeSpan.FromSeconds(30));
        var processId = 0;

        try
        {
            var session = await fixture.Application.LaunchAndAttachAsync(launchTarget, timeout.Token);
            processId = session.Target.Target.ProcessId;
            Assert.IsTrue(session.Target.Target.StartedAtUtc > DateTimeOffset.MinValue);
            Assert.AreEqual(executablePath, session.Target.Target.ExecutablePath, ignoreCase: true);
            Assert.AreSame(session, fixture.Application.ActiveSession);
            Assert.AreEqual(ProcessDiagnosticsSessionState.Monitoring, session.State);
        }
        finally
        {
            await fixture.Application.CloseAsync(CancellationToken.None);
            if (processId > 0)
            {
                await IntegrationTestHost.TerminateTargetAsync(processId);
            }
        }
    }

    /// <summary>
    /// 等待编排会话时间线收到真实的已测量样本。
    /// </summary>
    /// <param name="session">已附着的编排会话。</param>
    /// <param name="cancellationToken">取消等待的令牌。</param>
    /// <returns>第一条已测量样本。</returns>
    private static async Task<MemoryUsageSample> WaitForMeasuredSampleAsync(
        IAnalysisSession session,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var sample = session.Timeline
                .Snapshot()
                .LastOrDefault(candidate => candidate.State == MemoryUsageSampleState.Measured);
            if (sample is not null)
            {
                return sample;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        throw new AssertFailedException("The real orchestration session did not publish a measured memory sample within 30 seconds.");
    }
}
