namespace DotnetAnalysis.Application.Contracts.Diagnostics;

/// <summary>
/// 描述启动目标进程所需的稳定参数。
/// </summary>
public sealed record TargetProcessLaunchRequest
{
    /// <summary>创建目标进程启动请求。</summary>
    /// <param name="executablePath">要启动的 EXE 路径。</param>
    /// <param name="arguments">传给目标程序的参数。</param>
    /// <param name="workingDirectory">可选工作目录。</param>
    /// <param name="failureStrategy">启动等待失败后的处理策略。</param>
    /// <param name="startupTimeout">等待目标身份可读取的最长时间。</param>
    public TargetProcessLaunchRequest(
        string executablePath,
        string? arguments = null,
        string? workingDirectory = null,
        TargetProcessLaunchFailureStrategy failureStrategy = TargetProcessLaunchFailureStrategy.KeepProcess,
        TimeSpan? startupTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var timeout = startupTimeout ?? TimeSpan.FromSeconds(30);
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(startupTimeout), startupTimeout, "Startup timeout must be positive and finite.");
        }

        ExecutablePath = executablePath.Trim();
        Arguments = string.IsNullOrWhiteSpace(arguments) ? null : arguments.Trim();
        WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory.Trim();
        FailureStrategy = failureStrategy;
        StartupTimeout = timeout;
    }

    /// <summary>目标程序路径。</summary>
    public string ExecutablePath { get; }

    /// <summary>目标程序参数。</summary>
    public string? Arguments { get; }

    /// <summary>目标程序工作目录。</summary>
    public string? WorkingDirectory { get; }

    /// <summary>启动等待失败后的处理策略。</summary>
    public TargetProcessLaunchFailureStrategy FailureStrategy { get; }

    /// <summary>启动等待超时时间。</summary>
    public TimeSpan StartupTimeout { get; }
}

/// <summary>定义启动等待失败后的目标进程处理方式。</summary>
public enum TargetProcessLaunchFailureStrategy
{
    /// <summary>保留已启动的目标进程。</summary>
    KeepProcess,
    /// <summary>请求终止已启动的目标进程。</summary>
    TerminateProcess
}
