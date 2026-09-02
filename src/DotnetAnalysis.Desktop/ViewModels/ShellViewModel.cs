using DotnetAnalysis.Application.Events;
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
            eventBus.Subscribe<CaptureStarted>(HandleCaptureStartedAsync),
            eventBus.Subscribe<CaptureProgressChanged>(HandleCaptureProgressChangedAsync),
            eventBus.Subscribe<AnalysisCompleted>(HandleAnalysisCompletedAsync),
            eventBus.Subscribe<AnalysisCanceled>(HandleAnalysisCanceledAsync),
            eventBus.Subscribe<AnalysisFailed>(HandleAnalysisFailedAsync)
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

    private ValueTask HandleCaptureStartedAsync(CaptureStarted @event, CancellationToken cancellationToken) =>
        UpdateStatusAsync("正在捕获内存分配", cancellationToken);

    private ValueTask HandleCaptureProgressChangedAsync(
        CaptureProgressChanged @event,
        CancellationToken cancellationToken) =>
        UpdateStatusAsync($"正在捕获内存分配：{@event.Percent}%", cancellationToken);

    private ValueTask HandleAnalysisCompletedAsync(AnalysisCompleted @event, CancellationToken cancellationToken) =>
        UpdateStatusAsync("分析已完成", cancellationToken);

    private ValueTask HandleAnalysisCanceledAsync(AnalysisCanceled @event, CancellationToken cancellationToken) =>
        UpdateStatusAsync("分析已取消", cancellationToken);

    private ValueTask HandleAnalysisFailedAsync(AnalysisFailed @event, CancellationToken cancellationToken) =>
        UpdateStatusAsync($"分析失败：{@event.Message}", cancellationToken);

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
}
