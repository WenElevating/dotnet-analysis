using System.Windows.Input;

namespace DotnetAnalysis.Desktop.Infrastructure;

public sealed class AsyncRelayCommand : ObservableObject, ICommand
{
    private readonly Func<CancellationToken, Task> _executeAsync;
    private readonly Func<bool>? _canExecute;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _executionTask;
    private Exception? _lastError;
    private bool _isRunning;

    public AsyncRelayCommand(
        Func<CancellationToken, Task> executeAsync,
        Func<bool>? canExecute = null)
    {
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public event EventHandler? ExecutionFailed;

    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    public bool CanCancel => IsRunning && _cancellationTokenSource is not null;

    public Task? ExecutionTask
    {
        get => _executionTask;
        private set => SetProperty(ref _executionTask, value);
    }

    public Exception? LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    public bool CanExecute(object? parameter) => !IsRunning && (_canExecute?.Invoke() ?? true);

    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        StartExecution();
    }

    public void Cancel()
    {
        var cancellationTokenSource = _cancellationTokenSource;
        if (cancellationTokenSource is null)
        {
            return;
        }

        try
        {
            cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private void StartExecution()
    {
        var cancellationTokenSource = new CancellationTokenSource();
        _cancellationTokenSource = cancellationTokenSource;
        LastError = null;
        IsRunning = true;
        OnPropertyChanged(nameof(CanCancel));
        NotifyCanExecuteChanged();

        ExecutionTask = ExecuteCoreAsync(cancellationTokenSource);
    }

    private async Task ExecuteCoreAsync(CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            await _executeAsync(cancellationTokenSource.Token);
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LastError = exception;
            ExecutionFailed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            if (ReferenceEquals(_cancellationTokenSource, cancellationTokenSource))
            {
                _cancellationTokenSource = null;
                IsRunning = false;
                OnPropertyChanged(nameof(CanCancel));
                NotifyCanExecuteChanged();
            }

            cancellationTokenSource.Dispose();
        }
    }
}
