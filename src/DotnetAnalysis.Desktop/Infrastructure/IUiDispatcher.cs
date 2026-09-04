namespace DotnetAnalysis.Desktop.Infrastructure;

/// <summary>
/// 抽象把工作切换到桌面 UI 线程的调度器。
/// </summary>
public interface IUiDispatcher
{
    /// <summary>
    /// 将操作排入 UI 线程执行。
    /// </summary>
    /// <param name="action">要执行的 UI 操作。</param>
    /// <param name="cancellationToken">取消排队或执行的令牌。</param>
    /// <returns>表示 UI 操作完成的任务。</returns>
    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
}
