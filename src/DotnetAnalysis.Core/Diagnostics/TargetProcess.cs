namespace DotnetAnalysis.Core.Diagnostics;

public sealed record TargetProcess
{
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

    public int ProcessId { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public string ProcessName { get; }

    public string? ExecutablePath { get; }
}
