using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Orchestration.Models;

namespace DotnetAnalysis.Orchestration.Tests;

/// <summary>验证目标查找、能力探测和稳定启动契约。</summary>
[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe the required orchestration behavior.")]
public sealed class TargetProcessOrchestrationTests
{
    /// <summary>验证查找器按 PID 与启动时间去重、筛选、排序和分页。</summary>
    [TestMethod]
    public async Task Finder_DeduplicatesFiltersSortsAndPages()
    {
        var startedAt = new DateTimeOffset(2026, 9, 16, 1, 2, 3, TimeSpan.Zero);
        var diagnostics = new FakeProcessDiagnostics(
        [
            new TargetProcess(2, startedAt, "beta", null),
            new TargetProcess(1, startedAt, "alpha", null),
            new TargetProcess(1, startedAt, "alpha", null),
            new TargetProcess(3, startedAt, "other", null)
        ]);

        var finder = new TargetProcessFinder(diagnostics);
        var result = await finder.FindAsync(
            new ProcessFilter(processName: "a", pageSize: 2, sortBy: ProcessFilterSortField.ProcessId),
            CancellationToken.None);

        Assert.HasCount(2, result);
        Assert.AreEqual(1, result[0].ProcessId);
        Assert.AreEqual(2, result[1].ProcessId);
    }

    /// <summary>验证能力探测发现返回了不同目标身份时拒绝旧结果。</summary>
    [TestMethod]
    public async Task Probe_RejectsChangedIdentity()
    {
        var target = CreateTarget(7, "sample");
        var replacement = new TargetProcess(7, new DateTimeOffset(2026, 9, 16, 1, 2, 4, TimeSpan.Zero), "replacement", null);
        var diagnostics = new FakeProcessDiagnostics([], CreateCapabilities(replacement));
        var probe = new TargetCapabilityProbe(diagnostics);

        await Assert.ThrowsExactlyAsync<DiagnosticsException>(
            () => probe.ProbeAsync(target, CancellationToken.None));
    }

    /// <summary>验证能力证据按项映射且 Retention 不可用不会降级为可用。</summary>
    [TestMethod]
    public async Task Probe_PreservesIndependentCapabilityAvailability()
    {
        var target = CreateTarget(8, "sample");
        var evidence = new TargetProcessCapabilities(
            target,
            isWindows: true,
            is64BitOperatingSystem: true,
            is64BitTarget: true,
            isCoreClr: true,
            runtimeMajorVersion: 10,
            standardSnapshotAvailable: true,
            standardSnapshotErrorCode: null,
            standardSnapshotReason: null,
            retentionSnapshotAvailable: false,
            retentionSnapshotErrorCode: DiagnosticsErrorCode.ProfilerAttachUnavailable,
            retentionSnapshotReason: "Profiler 不可用",
            executionSamplingAvailable: true,
            executionSamplingErrorCode: null,
            executionSamplingReason: null,
            checkedAtUtc: DateTimeOffset.UtcNow);
        var probe = new TargetCapabilityProbe(new FakeProcessDiagnostics([], evidence));

        var context = await probe.ProbeAsync(target, CancellationToken.None);

        Assert.AreEqual(TargetArchitecture.X64, context.Architecture);
        Assert.AreEqual(".NET 10", context.Runtime);
        Assert.IsTrue(context.Capabilities.StandardSnapshot.IsAvailable);
        Assert.IsFalse(context.Capabilities.RetentionSnapshot.IsAvailable);
        Assert.AreEqual(
            DiagnosticsErrorCode.ProfilerAttachUnavailable,
            context.Capabilities.RetentionSnapshot.ErrorCode);
        Assert.IsTrue(context.Capabilities.ExecutionSampling.IsAvailable);
    }

    /// <summary>验证启动请求拒绝空路径和无效超时。</summary>
    [TestMethod]
    public void LaunchRequest_ValidatesPathAndTimeout()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new TargetProcessLaunchRequest(" "));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new TargetProcessLaunchRequest("sample.exe", startupTimeout: TimeSpan.Zero));
        Assert.AreEqual(
            TargetProcessLaunchFailureStrategy.KeepProcess,
            new TargetProcessLaunchRequest("sample.exe").FailureStrategy);
    }

    private static TargetProcess CreateTarget(int processId, string name) =>
        new(processId, new DateTimeOffset(2026, 9, 16, 1, 2, 3, TimeSpan.Zero), name, null);

    private static TargetProcessCapabilities CreateCapabilities(TargetProcess target) =>
        new(
            target,
            isWindows: true,
            is64BitOperatingSystem: true,
            is64BitTarget: true,
            isCoreClr: true,
            runtimeMajorVersion: 10,
            standardSnapshotAvailable: true,
            standardSnapshotErrorCode: null,
            standardSnapshotReason: null,
            retentionSnapshotAvailable: true,
            retentionSnapshotErrorCode: null,
            retentionSnapshotReason: null,
            executionSamplingAvailable: true,
            executionSamplingErrorCode: null,
            executionSamplingReason: null,
            checkedAtUtc: DateTimeOffset.UtcNow);

    private sealed class FakeProcessDiagnostics : IProcessDiagnostics
    {
        private readonly IReadOnlyList<TargetProcess> _processes;
        private readonly TargetProcessCapabilities? _capabilities;

        public FakeProcessDiagnostics(
            IReadOnlyList<TargetProcess> processes,
            TargetProcessCapabilities? capabilities = null)
        {
            _processes = processes;
            _capabilities = capabilities;
        }

        public Task<IReadOnlyList<TargetProcess>> GetProcessesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_processes);
        }

        public Task<TargetProcessCapabilities> ProbeCapabilitiesAsync(
            TargetProcess process,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_capabilities ?? CreateCapabilities(process));
        }

        public Task<IProcessDiagnosticsSession> AttachAsync(
            TargetProcess process,
            CancellationToken cancellationToken) =>
            Task.FromException<IProcessDiagnosticsSession>(
                new DiagnosticsException(DiagnosticsErrorCode.RuntimeNotSupported, "测试替身不支持附着。"));

        public Task<MemorySnapshot> OpenSnapshotAsync(
            string filePath,
            CancellationToken cancellationToken) =>
            Task.FromException<MemorySnapshot>(
                new DiagnosticsException(DiagnosticsErrorCode.SnapshotFormatNotSupported, "测试替身不支持快照。"));
    }
}





