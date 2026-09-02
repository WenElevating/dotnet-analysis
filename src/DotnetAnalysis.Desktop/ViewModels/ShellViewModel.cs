using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Desktop.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DotnetAnalysis.Desktop.ViewModels;

public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private static readonly Action<ILogger, Exception?> s_uiDispatcherUpdateFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(1, "UiDispatcherUpdateFailed"),
        "Could not update the shell state on the UI dispatcher.");

    private readonly IUiDispatcher _uiDispatcher;
    private readonly ILogger<ShellViewModel> _logger;
    private readonly IDisposable[] _subscriptions;
    private string _statusText = "尚未开始分析";

    public ShellViewModel(
        IEventBus eventBus,
        IUiDispatcher uiDispatcher,
        ILogger<ShellViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(eventBus);
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _subscriptions =
        [
            eventBus.Subscribe<ProcessDiagnosticsSessionStateChanged>(HandleSessionStateChangedAsync),
            eventBus.Subscribe<ProcessDiagnosticsSessionEnded>(HandleSessionEndedAsync),
            eventBus.Subscribe<MemorySnapshotCaptureStarted>(HandleSnapshotCaptureStartedAsync),
            eventBus.Subscribe<MemorySnapshotCaptured>(HandleSnapshotCapturedAsync),
            eventBus.Subscribe<MemorySnapshotCaptureFailed>(HandleSnapshotCaptureFailedAsync),
            eventBus.Subscribe<MemorySnapshotAnalysisStarted>(HandleSnapshotAnalysisStartedAsync),
            eventBus.Subscribe<MemorySnapshotAnalysisCompleted>(HandleSnapshotAnalysisCompletedAsync),
            eventBus.Subscribe<MemorySnapshotAnalysisFailed>(HandleSnapshotAnalysisFailedAsync),
            eventBus.Subscribe<ProcessMemoryUsageUpdated>(HandleProcessMemoryUsageUpdatedAsync),
            eventBus.Subscribe<AllocationSamplingStatusChanged>(HandleAllocationSamplingStatusChangedAsync)
        ];
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
    }

    private ValueTask HandleSessionStateChangedAsync(
        ProcessDiagnosticsSessionStateChanged @event,
        CancellationToken cancellationToken) =>
        UpdateStatusAsync($"诊断会话状态：{GetSessionStateText(@event.State)}", cancellationToken);

    private ValueTask HandleSessionEndedAsync(
        ProcessDiagnosticsSessionEnded @event,
        CancellationToken cancellationToken) =>
        UpdateStatusAsync("诊断会话已结束", cancellationToken);

    private ValueTask HandleSnapshotCaptureStartedAsync(
        MemorySnapshotCaptureStarted @event,
        CancellationToken cancellationToken) =>
        UpdateStatusAsync("正在捕获内存快照", cancellationToken);

    private ValueTask HandleSnapshotCapturedAsync(
        MemorySnapshotCaptured @event,
        CancellationToken cancellationToken) =>
        UpdateStatusAsync("快照已保存，等待分析", cancellationToken);

    private ValueTask HandleSnapshotCaptureFailedAsync(
        MemorySnapshotCaptureFailed @event,
        CancellationToken cancellationToken) =>
        UpdateStatusAsync($"快照捕获失败：{GetErrorText(@event.ErrorCode)}", cancellationToken);

    private ValueTask HandleSnapshotAnalysisStartedAsync(
        MemorySnapshotAnalysisStarted @event,
        CancellationToken cancellationToken) =>
        UpdateStatusAsync("正在分析快照", cancellationToken);

    private ValueTask HandleSnapshotAnalysisCompletedAsync(
        MemorySnapshotAnalysisCompleted @event,
        CancellationToken cancellationToken) =>
        UpdateStatusAsync("快照分析已完成", cancellationToken);

    private ValueTask HandleSnapshotAnalysisFailedAsync(
        MemorySnapshotAnalysisFailed @event,
        CancellationToken cancellationToken) =>
        UpdateStatusAsync($"快照分析失败：{GetErrorText(@event.ErrorCode)}", cancellationToken);

    private ValueTask HandleAllocationSamplingStatusChangedAsync(
        AllocationSamplingStatusChanged @event,
        CancellationToken cancellationToken) =>
        UpdateStatusAsync($"分配采样：{@event.Status}", cancellationToken);

    private ValueTask HandleProcessMemoryUsageUpdatedAsync(
        ProcessMemoryUsageUpdated @event,
        CancellationToken cancellationToken)
    {
        if (@event.Sample.ProcessMemoryBytes is null)
        {
            return UpdateStatusAsync("进程内存暂不可用", cancellationToken);
        }

        var processMemoryMegabytes = @event.Sample.ProcessMemoryBytes.Value / 1024 / 1024;
        return UpdateStatusAsync($"进程内存：{processMemoryMegabytes} MB", cancellationToken);
    }

    private async ValueTask UpdateStatusAsync(string statusText, CancellationToken cancellationToken)
    {
        try
        {
            await _uiDispatcher
                .InvokeAsync(() => StatusText = statusText, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            s_uiDispatcherUpdateFailed(_logger, exception);
        }
    }

    private static string GetSessionStateText(ProcessDiagnosticsSessionState state) =>
        state switch
        {
            ProcessDiagnosticsSessionState.Attaching => "正在附着",
            ProcessDiagnosticsSessionState.Monitoring => "监控中",
            ProcessDiagnosticsSessionState.Ending => "正在结束",
            ProcessDiagnosticsSessionState.Ended => "已结束",
            ProcessDiagnosticsSessionState.Failed => "失败",
            _ => "未知"
        };

    private static string GetErrorText(DiagnosticsErrorCode errorCode) =>
        errorCode switch
        {
            DiagnosticsErrorCode.AccessDenied => "访问被拒绝",
            DiagnosticsErrorCode.TargetExited => "目标已退出",
            DiagnosticsErrorCode.TargetChanged => "目标已变化",
            DiagnosticsErrorCode.RuntimeNotSupported => "运行时不受支持",
            DiagnosticsErrorCode.SnapshotFormatNotSupported => "快照格式不受支持",
            DiagnosticsErrorCode.CaptureFailed => "捕获失败",
            DiagnosticsErrorCode.CaptureCancelled => "捕获已取消",
            _ => "未知错误"
        };
}
