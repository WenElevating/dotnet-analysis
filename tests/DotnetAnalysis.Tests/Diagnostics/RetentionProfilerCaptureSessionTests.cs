using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Tests.Diagnostics;

/// <summary>
/// 验证受管端为原生保留 Profiler 创建的共享内存会话边界。
/// </summary>
[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not have incorrect suffix", Justification = "测试名说明边界行为。")]
public sealed class RetentionProfilerCaptureSessionTests
{
    /// <summary>
    /// 原生附加必须由独立受管会话拥有映射和事件生命周期，不能泄漏到调用方。
    /// </summary>
    [TestMethod]
    public void RetentionProfilerCaptureSession_ExistsAsAnIndependentInteropBoundary()
    {
        var type = typeof(MemorySnapshotStore).Assembly.GetType(
            "DotnetAnalysis.Diagnostics.Windows.RetentionProfilerCaptureSession");

        Assert.IsNotNull(type);
    }

    /// <summary>
    /// 托管共享结构必须和 Windows x64 原生协议保持固定大小，否则 Profiler 会在错误偏移读写。
    /// </summary>
    [TestMethod]
    public void SharedMemoryRecords_MatchTheNativeX64ProtocolLayout()
    {
        Assert.AreEqual(88, Marshal.SizeOf<RetentionProfilerSharedHeader>());
        Assert.AreEqual(24, Marshal.SizeOf<RetentionProfilerRawObject>());
        Assert.AreEqual(16, Marshal.SizeOf<RetentionProfilerRawEdge>());
        Assert.AreEqual(32, Marshal.SizeOf<RetentionProfilerRawRoot>());
        Assert.AreEqual(1_552, Marshal.SizeOf<RetentionProfilerTypeEvidenceRecord>());
        Assert.AreEqual(2088, Marshal.SizeOf<RetentionProfilerAttachData>());
    }

    /// <summary>
    /// 取消等待 CLR 分离只应取消调用方的等待，不能被 15 秒的原生超时阻塞。
    /// </summary>
    [TestMethod]
    public async Task WaitForDetachAsync_WhenCancellationOccursAfterWaitingBegins_StopsPromptly()
    {
        using var capture = new RetentionProfilerCaptureSession();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var waitTask = capture.WaitForDetachAsync(cancellationSource.Token);
        var completedTask = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.AreSame(waitTask, completedTask, "取消必须中断分离等待，不能等待原生 15 秒超时。");
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await waitTask);
    }
}
