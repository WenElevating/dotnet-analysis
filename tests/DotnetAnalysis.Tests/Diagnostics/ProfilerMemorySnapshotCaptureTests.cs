using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;
using DotnetAnalysis.Diagnostics.Windows.Capture;

namespace DotnetAnalysis.Tests.Diagnostics;

/// <summary>
/// 验证保留分析捕获在取得存储租约后、执行原生附加前重新确认目标进程身份。
/// </summary>
[TestClass]
[DoNotParallelize]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "测试名称描述捕获准入行为。")]
public sealed class ProfilerMemorySnapshotCaptureTests
{
    /// <summary>
    /// 排队取得存储租约后若 PID 已指向不同启动时间，捕获必须拒绝替代进程而不能继续到 Profiler 附加。
    /// </summary>
    [TestMethod]
    public async Task CaptureAsync_WhenProcessIdentityChangesWhileStorageReservationIsQueued_RejectsBeforeAttach()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var expectedStartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            var identitySource = new SequencedProcessIdentitySource(
                expectedStartedAtUtc,
                expectedStartedAtUtc.AddSeconds(1));
            var layout = new SnapshotStorageLayout(root);
            var capture = new ProfilerMemorySnapshotCapture(
                new ProcessIdentityValidator(identitySource),
                layout,
                new MemorySnapshotStore(layout, new ImportedSnapshotCatalog()),
                new ImmediateStorageGuard(),
                TimeProvider.System);
            await using var allocationCollector = new AllocationSamplingSession(
                new AllocationProfileBuilder(DateTimeOffset.UtcNow));
            var target = new TargetProcess(int.MaxValue, expectedStartedAtUtc, "reused-pid", null);

            var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(async () =>
                await capture.CaptureAsync(
                    MemorySnapshotCaptureMode.RetentionAnalysis,
                    target,
                    allocationCollector,
                    CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.TargetChanged, exception.ErrorCode);
            Assert.AreEqual(2, identitySource.ReadCount);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// 依次返回捕获前身份和租约后替代身份，精确模拟排队期间发生的 PID 复用。
    /// </summary>
    private sealed class SequencedProcessIdentitySource : IProcessIdentitySource
    {
        private readonly DateTimeOffset _first;
        private readonly DateTimeOffset _subsequent;

        /// <summary>
        /// 创建具有两个确定返回阶段的身份源。
        /// </summary>
        public SequencedProcessIdentitySource(DateTimeOffset first, DateTimeOffset subsequent)
        {
            _first = first;
            _subsequent = subsequent;
        }

        /// <summary>
        /// 已执行的身份读取次数。
        /// </summary>
        public int ReadCount { get; private set; }

        /// <inheritdoc />
        public DateTimeOffset GetStartedAtUtc(int processId)
        {
            ReadCount++;
            return ReadCount == 1 ? _first : _subsequent;
        }
    }

    /// <summary>
    /// 立即授予捕获租约，使测试只观察租约后的二次身份检查。
    /// </summary>
    private sealed class ImmediateStorageGuard : IRetentionSnapshotStorageGuard
    {
        /// <inheritdoc />
        public Task EnsureCanStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <inheritdoc />
        public Task EnsureCanStoreAsync(string temporaryPath, CancellationToken cancellationToken) => Task.CompletedTask;

        /// <inheritdoc />
        public Task<IRetentionSnapshotCaptureReservation> ReserveCaptureAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IRetentionSnapshotCaptureReservation>(new ImmediateCaptureReservation());

        /// <inheritdoc />
        public Task<IDisposable> ReservePromotionAsync(string temporaryPath, CancellationToken cancellationToken) =>
            Task.FromResult<IDisposable>(new ImmediateCaptureReservation());
    }

    /// <summary>
    /// 不占用外部资源的测试捕获租约。
    /// </summary>
    private sealed class ImmediateCaptureReservation : IRetentionSnapshotCaptureReservation
    {
        /// <inheritdoc />
        public Task EnsureCanStoreAsync(string temporaryPath, CancellationToken cancellationToken) => Task.CompletedTask;

        /// <inheritdoc />
        public void Dispose()
        {
        }
    }
}
