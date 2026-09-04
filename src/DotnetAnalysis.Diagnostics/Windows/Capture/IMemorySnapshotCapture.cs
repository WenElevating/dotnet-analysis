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
    /// 为目标进程生成并持久化一个可分析的内存快照。
    /// </summary>
    Task<MemorySnapshot> CaptureAsync(
        TargetProcess target,
        AllocationSampleCollector allocationCollector,
        CancellationToken cancellationToken);
}
