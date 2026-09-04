using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Desktop.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DotnetAnalysis.Desktop.ViewModels;

/// <summary>
/// 订阅诊断事件并向桌面壳层公开可绑定状态文本。
/// </summary>
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

    /// <summary>
    /// 订阅诊断生命周期事件并把状态投影到桌面壳层。
    /// </summary>
    /// <param name="eventBus">提供诊断事件的进程内事件总线。</param>
    /// <param name="uiDispatcher">用于切换到 UI 线程的调度器。</param>
    /// <param name="logger">记录 UI 更新失败的日志记录器。</param>
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

    /// <summary>
    /// 面向用户显示的当前诊断状态文本。
    /// </summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>
    /// 释放所有事件订阅；释放后不再接收诊断更新。
    /// </summary>
    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
    }

    /// <summary>
    /// 处理诊断会话状态变化事件。
    /// </summary>
    private ValueTask HandleSessionStateChangedAsync(
        ProcessDiagnosticsSessionStateChanged @event,
        CancellationToken cancellationToken) =>
        UpdateStatusAsync($"诊断会话状态：{GetSessionStateText(@event.State)}", cancellationToken);

    /// <summary>
    /// 处理诊断会话结束事件。
    /// </summary>
    private ValueTask HandleSessionEndedAsync(
        ProcessDiagnosticsSessionEnded @event,
        CancellationToken cancellationToken) =>
        UpdateStatusAsync("诊断会话已结束", cancellationToken);

    /// <summary>
    /// 处理快照捕获开始事件。
    /// </summary>
    private ValueTask HandleSnapshotCaptureStartedAsync(
        MemorySnapshotCaptureStarted @event,
        CancellationToken cancellationToken) =>
        UpdateStatusAsync("正在捕获内存快照", cancellationToken);

    /// <summary>
    /// 处理快照捕获完成事件。
    /// </summary>
    private ValueTask HandleSnapshotCapturedAsync(
        MemorySnapshotCaptured @event,
        CancellationToken cancellationToken) =>
        UpdateStatusAsync("快照已保存，等待分析", cancellationToken);

    /// <summary>
    /// 处理快照捕获失败事件。
    /// </summary>
    private ValueTask HandleSnapshotCaptureFailedAsync(
        MemorySnapshotCaptureFailed @event,
        CancellationToken cancellationToken) =>
        UpdateStatusAsync($"快照捕获失败：{GetErrorText(@event.ErrorCode)}", cancellationToken);

    /// <summary>
    /// 处理快照分析开始事件。
    /// </summary>
    private ValueTask HandleSnapshotAnalysisStartedAsync(
        MemorySnapshotAnalysisStarted @event,
        CancellationToken cancellationToken) =>
        UpdateStatusAsync("正在分析快照", cancellationToken);

    /// <summary>
    /// 处理快照分析完成事件。
    /// </summary>
    private ValueTask HandleSnapshotAnalysisCompletedAsync(
        MemorySnapshotAnalysisCompleted @event,
        CancellationToken cancellationToken) =>
        UpdateStatusAsync("快照分析已完成", cancellationToken);

    /// <summary>
    /// 处理快照分析失败事件。
    /// </summary>
    private ValueTask HandleSnapshotAnalysisFailedAsync(
        MemorySnapshotAnalysisFailed @event,
        CancellationToken cancellationToken) =>
        UpdateStatusAsync($"快照分析失败：{GetErrorText(@event.ErrorCode)}", cancellationToken);

    /// <summary>
    /// 处理分配采样状态变化事件。
    /// </summary>
    private ValueTask HandleAllocationSamplingStatusChangedAsync(
        AllocationSamplingStatusChanged @event,
        CancellationToken cancellationToken) =>
        UpdateStatusAsync($"分配采样：{@event.Status}", cancellationToken);

    /// <summary>
    /// 处理进程内存使用更新事件并刷新状态文本。
    /// </summary>
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

    /// <summary>
    /// 通过 UI 调度器更新状态文本并隔离调度异常。
    /// </summary>
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

    /// <summary>
    /// 把核心会话状态转换为用户可读文本。
    /// </summary>
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

    /// <summary>
    /// 把诊断错误码转换为用户可读文本。
    /// </summary>
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
