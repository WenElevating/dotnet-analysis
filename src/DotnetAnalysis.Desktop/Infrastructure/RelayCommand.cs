using System.Windows.Input;

namespace DotnetAnalysis.Desktop.Infrastructure;

/// <summary>
/// 把同步委托适配为可绑定的 WPF 命令。
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    /// <summary>
    /// 创建一个同步委托命令。
    /// </summary>
    /// <param name="execute">命令执行委托。</param>
    /// <param name="canExecute">可选的可执行条件。</param>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>
    /// 命令可执行状态变化时触发。
    /// </summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>
    /// 判断当前命令是否允许执行。
    /// </summary>
    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    /// <summary>
    /// 执行命令；参数由 WPF 命令系统传入但不参与计算。
    /// </summary>
    public void Execute(object? parameter) => _execute();

    /// <summary>
    /// 通知绑定目标重新查询可执行状态。
    /// </summary>
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
