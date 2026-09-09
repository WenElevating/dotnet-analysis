using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;
using System.Collections.Frozen;

namespace DotnetAnalysis.Diagnostics.Windows.Capture;

/// <summary>
/// 按捕获方式将会话请求路由到唯一内部捕获器，并禁止保留分析回退到轻量捕获。
/// </summary>
internal sealed class MemorySnapshotCaptureRegistry : IMemorySnapshotCapture
{
    private readonly Dictionary<MemorySnapshotCaptureMode, IMemorySnapshotCapture> _captures;
    private readonly IReadOnlySet<MemorySnapshotCaptureMode> _supportedCaptureModes;

    /// <summary>
    /// 从已注册捕获器构造唯一方式路由表。
    /// </summary>
    /// <param name="captures">可处理一种或多种捕获方式的内部捕获器。</param>
    /// <exception cref="ArgumentNullException">捕获器集合为空时引发。</exception>
    /// <exception cref="ArgumentException">方式未声明或被多个捕获器重复声明时引发。</exception>
    public MemorySnapshotCaptureRegistry(IEnumerable<IMemorySnapshotCapture> captures)
    {
        ArgumentNullException.ThrowIfNull(captures);
        _captures = [];
        foreach (var capture in captures)
        {
            ArgumentNullException.ThrowIfNull(capture);
            if (capture.SupportedCaptureModes.Count == 0)
            {
                throw new ArgumentException("快照捕获器必须声明至少一种捕获方式。", nameof(captures));
            }

            foreach (var captureMode in capture.SupportedCaptureModes)
            {
                if (!Enum.IsDefined(captureMode) || !_captures.TryAdd(captureMode, capture))
                {
                    throw new ArgumentException("每种快照捕获方式只能注册一个有效捕获器。", nameof(captures));
                }
            }
        }

        if (_captures.Count == 0)
        {
            throw new ArgumentException("必须至少注册一个快照捕获器。", nameof(captures));
        }

        _supportedCaptureModes = _captures.Keys.ToFrozenSet();
    }

    /// <inheritdoc />
    public IReadOnlySet<MemorySnapshotCaptureMode> SupportedCaptureModes => _supportedCaptureModes;

    /// <inheritdoc />
    public Task<MemorySnapshot> CaptureAsync(
        MemorySnapshotCaptureMode captureMode,
        TargetProcess target,
        AllocationSamplingSession allocationCollector,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(captureMode))
        {
            throw new ArgumentOutOfRangeException(nameof(captureMode));
        }

        if (!_captures.TryGetValue(captureMode, out var capture))
        {
            var errorCode = captureMode is MemorySnapshotCaptureMode.RetentionAnalysis
                ? DiagnosticsErrorCode.ProfilerAttachUnavailable
                : DiagnosticsErrorCode.CaptureFailed;
            throw new DiagnosticsException(errorCode, "请求的快照捕获方式在当前诊断环境中不可用。");
        }

        return capture.CaptureAsync(captureMode, target, allocationCollector, cancellationToken);
    }
}
