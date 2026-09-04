namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示用于附着和身份验证的目标进程快照。
/// </summary>
public sealed record TargetProcess
{
    /// <summary>
    /// 创建目标进程身份。
    /// </summary>
    /// <param name="processId">正数进程 ID。</param>
    /// <param name="startedAtUtc">进程启动时间，用于防止 PID 复用。</param>
    /// <param name="processName">进程显示名称。</param>
    /// <param name="executablePath">可选可执行文件路径；权限不足时为空。</param>
    public TargetProcess(
        int processId,
        DateTimeOffset startedAtUtc,
        string processName,
        string? executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), processId, "Process ID must be positive.");
        }

        ProcessId = processId;
        StartedAtUtc = startedAtUtc;
        ProcessName = processName;
        ExecutablePath = executablePath;
    }

    /// <summary>
    /// 操作系统分配的进程 ID。
    /// </summary>
    public int ProcessId { get; }

    /// <summary>
    /// 进程启动时间，用于身份验证。
    /// </summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>
    /// 进程显示名称。
    /// </summary>
    public string ProcessName { get; }

    /// <summary>
    /// 可执行文件路径；无法读取时为空。
    /// </summary>
    public string? ExecutablePath { get; }
}
