namespace DotnetAnalysis.Orchestration.Models;

/// <summary>
/// 描述启动并附着目标进程所需的宿主无关参数。
/// </summary>
public sealed record LaunchTarget
{
    /// <summary>创建启动目标。</summary>
    /// <param name="executablePath">要启动的 EXE 路径。</param>
    /// <param name="arguments">传给目标进程的参数。</param>
    /// <param name="workingDirectory">可选工作目录。</param>
    /// <param name="strategy">启动后的目标处理策略。</param>
    /// <param name="startupTimeout">等待目标可附着的最长时间。</param>
    public LaunchTarget(
        string executablePath,
        string? arguments = null,
        string? workingDirectory = null,
        LaunchTargetStrategy strategy = LaunchTargetStrategy.KeepOnFailure,
        TimeSpan? startupTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var timeout = startupTimeout ?? TimeSpan.FromSeconds(30);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(startupTimeout), startupTimeout, "Startup timeout must be positive.");
        }

        ExecutablePath = executablePath.Trim();
        Arguments = string.IsNullOrWhiteSpace(arguments) ? null : arguments.Trim();
        WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory.Trim();
        Strategy = strategy;
        StartupTimeout = timeout;
    }

    /// <summary>目标 EXE 路径。</summary>
    public string ExecutablePath { get; }

    /// <summary>启动参数。</summary>
    public string? Arguments { get; }

    /// <summary>目标工作目录。</summary>
    public string? WorkingDirectory { get; }

    /// <summary>启动失败时是否保留目标进程。</summary>
    public LaunchTargetStrategy Strategy { get; }

    /// <summary>等待目标可附着的最长时间。</summary>
    public TimeSpan StartupTimeout { get; }
}

/// <summary>启动目标失败后的处理策略。</summary>
public enum LaunchTargetStrategy
{
    /// <summary>失败时保留已启动进程。</summary>
    KeepOnFailure,
    /// <summary>失败时请求终止已启动进程。</summary>
    TerminateOnFailure
}
