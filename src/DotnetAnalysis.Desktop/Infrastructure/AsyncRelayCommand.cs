using System.Windows.Input;

namespace DotnetAnalysis.Desktop.Infrastructure;

/// <summary>
/// 把支持取消的异步委托适配为可绑定的 WPF 命令。
/// </summary>
public sealed class AsyncRelayCommand : ObservableObject, ICommand
{
    private readonly Func<CancellationToken, Task> _executeAsync;
    private readonly Func<bool>? _canExecute;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _executionTask;
    private Exception? _lastError;
    private bool _isRunning;

    /// <summary>
    /// 创建一个支持取消、状态通知和异常回传的异步命令。
    /// </summary>
    /// <param name="executeAsync">接收取消令牌的异步执行委托。</param>
    /// <param name="canExecute">可选的可执行条件。</param>
    public AsyncRelayCommand(
        Func<CancellationToken, Task> executeAsync,
        Func<bool>? canExecute = null)
    {
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute;
    }

    /// <summary>
    /// 命令可执行状态变化时触发。
    /// </summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>
    /// 异步执行失败且异常已保存到 <see cref="LastError"/> 时触发。
    /// </summary>
    public event EventHandler? ExecutionFailed;

    /// <summary>
    /// 当前是否有异步执行正在进行。
    /// </summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    /// <summary>
    /// 当前执行是否可以请求取消。
    /// </summary>
    public bool CanCancel => IsRunning && _cancellationTokenSource is not null;

    /// <summary>
    /// 当前执行任务；未开始执行时为 <see langword="null"/>。
    /// </summary>
    public Task? ExecutionTask
    {
        get => _executionTask;
        private set => SetProperty(ref _executionTask, value);
    }

    /// <summary>
    /// 最近一次非取消执行的异常。
    /// </summary>
    public Exception? LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    /// <summary>
    /// 判断命令当前是否允许启动新的执行。
    /// </summary>
    public bool CanExecute(object? parameter) => !IsRunning && (_canExecute?.Invoke() ?? true);

    /// <summary>
    /// 启动一次异步执行；不可执行时静默返回。
    /// </summary>
    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        StartExecution();
    }

    /// <summary>
    /// 请求取消当前执行；没有执行或已完成时不产生影响。
    /// </summary>
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

    /// <summary>
    /// 通知绑定目标重新查询可执行状态。
    /// </summary>
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 创建执行取消源并启动异步工作。
    /// </summary>
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

    /// <summary>
    /// 执行委托并把异常、取消和完成状态投影到命令属性。
    /// </summary>
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
