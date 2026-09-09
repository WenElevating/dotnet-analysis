using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Diagnostics.Windows.Capture;

/// <summary>
/// 定义单次进程内存快照捕获的内部边界。
/// </summary>
internal interface IMemorySnapshotCapture
{
    /// <summary>
    /// 此捕获器可以处理的捕获方式；每种方式在注册表中只能有一个处理器。
    /// </summary>
    IReadOnlySet<MemorySnapshotCaptureMode> SupportedCaptureModes { get; }

    /// <summary>
    /// 为目标进程使用指定方式生成并持久化一个可分析的内存快照。
    /// </summary>
    Task<MemorySnapshot> CaptureAsync(
        MemorySnapshotCaptureMode captureMode,
        TargetProcess target,
        AllocationSamplingSession allocationCollector,
        CancellationToken cancellationToken);
}
