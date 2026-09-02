using System.Windows;
using System.Windows.Threading;

namespace DotnetAnalysis.Desktop.Infrastructure;

public sealed class WpfUiDispatcher : IUiDispatcher
{
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
