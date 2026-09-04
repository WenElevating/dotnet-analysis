using System.Windows;
using System.Windows.Threading;

namespace DotnetAnalysis.Desktop.Infrastructure;

/// <summary>
/// 使用当前 WPF 应用调度器执行 UI 线程操作。
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    /// <summary>
    /// 把操作提交到当前 WPF 应用的主调度器。
    /// </summary>
    /// <param name="action">要在 UI 线程执行的操作。</param>
    /// <param name="cancellationToken">取消排队或执行的令牌。</param>
    /// <returns>表示 UI 操作完成的任务。</returns>
    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();

        var application = System.Windows.Application.Current
            ?? throw new InvalidOperationException("The WPF application has not been initialized.");
        return application.Dispatcher
            .InvokeAsync(action, DispatcherPriority.Normal, cancellationToken)
            .Task;
    }
}
