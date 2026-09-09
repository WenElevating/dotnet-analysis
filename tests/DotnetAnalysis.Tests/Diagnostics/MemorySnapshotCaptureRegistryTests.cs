using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;
using DotnetAnalysis.Diagnostics.Windows.Capture;

namespace DotnetAnalysis.Tests.Diagnostics;

/// <summary>
/// 验证快照捕获方式到内部捕获器的唯一且不降级路由规则。
/// </summary>
[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "测试名称描述路由行为。")]
public sealed class MemorySnapshotCaptureRegistryTests
{
    /// <summary>
    /// 验证保留分析请求只会调用专用捕获器，不会回退到标准捕获器。
    /// </summary>
    [TestMethod]
    public async Task CaptureAsync_WhenRetentionAnalysisIsRequested_RoutesOnlyToRetentionCapture()
    {
        var standard = new RecordingCapture(MemorySnapshotCaptureMode.Standard);
        var retention = new RecordingCapture(MemorySnapshotCaptureMode.RetentionAnalysis);
        var registry = new MemorySnapshotCaptureRegistry([standard, retention]);

        _ = await registry.CaptureAsync(
            MemorySnapshotCaptureMode.RetentionAnalysis,
            Target(),
            new AllocationSamplingSession(new AllocationProfileBuilder(DateTimeOffset.UtcNow)),
            CancellationToken.None);

        Assert.AreEqual(0, standard.Calls);
        Assert.AreEqual(1, retention.Calls);
    }

    /// <summary>
    /// 验证未注册的保留分析捕获方式会返回稳定附加不可用错误而非静默降级。
    /// </summary>
    [TestMethod]
    public async Task CaptureAsync_WhenRetentionAnalysisIsNotRegistered_ThrowsProfilerAttachUnavailable()
    {
        var registry = new MemorySnapshotCaptureRegistry([new RecordingCapture(MemorySnapshotCaptureMode.Standard)]);

        var exception = await Assert.ThrowsAsync<DiagnosticsException>(async () => await registry.CaptureAsync(
            MemorySnapshotCaptureMode.RetentionAnalysis,
            Target(),
            new AllocationSamplingSession(new AllocationProfileBuilder(DateTimeOffset.UtcNow)),
            CancellationToken.None));

        Assert.AreEqual(DiagnosticsErrorCode.ProfilerAttachUnavailable, exception.ErrorCode);
    }

    /// <summary>
    /// 创建仅用于捕获器路由测试的稳定目标进程描述。
    /// </summary>
    private static TargetProcess Target() => new(1234, DateTimeOffset.UtcNow, "target", null);

    /// <summary>
    /// 记录一次捕获调用的最小内部捕获器替身。
    /// </summary>
    private sealed class RecordingCapture(MemorySnapshotCaptureMode supportedMode) : IMemorySnapshotCapture
    {
        private readonly IReadOnlySet<MemorySnapshotCaptureMode> _supportedCaptureModes =
            new HashSet<MemorySnapshotCaptureMode> { supportedMode };

        /// <summary>
        /// 已被路由的调用次数。
        /// </summary>
        public int Calls { get; private set; }

        /// <summary>
        /// 捕获器接受的唯一方式。
        /// </summary>
        public MemorySnapshotCaptureMode SupportedMode { get; } = supportedMode;

        public IReadOnlySet<MemorySnapshotCaptureMode> SupportedCaptureModes => _supportedCaptureModes;

        /// <inheritdoc />
        public Task<MemorySnapshot> CaptureAsync(
            MemorySnapshotCaptureMode captureMode,
            TargetProcess target,
            AllocationSamplingSession allocationCollector,
            CancellationToken cancellationToken)
        {
            Assert.AreEqual(SupportedMode, captureMode);
            Calls++;
            return Task.FromResult(new MemorySnapshot(
                MemorySnapshotId.New(),
                MemorySnapshotOrigin.Captured,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                MemorySnapshotState.Analyzing));
        }
    }
}
