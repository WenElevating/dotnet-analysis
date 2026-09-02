namespace DotnetAnalysis.Desktop.Infrastructure;

public interface IUiDispatcher
{
    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
}
