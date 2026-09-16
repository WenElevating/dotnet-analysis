namespace DotnetAnalysis.Orchestration.Models;

/// <summary>
/// 描述目标进程查找条件和结果分页参数，不包含任何宿主 UI 类型。
/// </summary>
public sealed record ProcessFilter
{
    /// <summary>创建进程筛选条件。</summary>
    /// <param name="processName">可选进程名称。</param>
    /// <param name="executablePath">可选可执行文件路径。</param>
    /// <param name="runtime">可选运行时名称。</param>
    /// <param name="architecture">可选目标架构。</param>
    /// <param name="pageSize">结果页大小，必须为正数。</param>
    /// <param name="sortBy">排序字段。</param>
    /// <param name="descending">是否倒序。</param>
    public ProcessFilter(
        string? processName = null,
        string? executablePath = null,
        string? runtime = null,
        TargetArchitecture? architecture = null,
        int pageSize = 100,
        ProcessFilterSortField sortBy = ProcessFilterSortField.ProcessName,
        bool descending = false)
    {
        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "Page size must be positive.");
        }

        ProcessName = Normalize(processName);
        ExecutablePath = Normalize(executablePath);
        Runtime = Normalize(runtime);
        Architecture = architecture;
        PageSize = pageSize;
        SortBy = sortBy;
        Descending = descending;
    }

    /// <summary>进程名称筛选条件。</summary>
    public string? ProcessName { get; }

    /// <summary>可执行文件路径筛选条件。</summary>
    public string? ExecutablePath { get; }

    /// <summary>运行时筛选条件。</summary>
    public string? Runtime { get; }

    /// <summary>架构筛选条件。</summary>
    public TargetArchitecture? Architecture { get; }

    /// <summary>结果页大小。</summary>
    public int PageSize { get; }

    /// <summary>排序字段。</summary>
    public ProcessFilterSortField SortBy { get; }

    /// <summary>是否使用倒序。</summary>
    public bool Descending { get; }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>目标进程候选的稳定排序字段。</summary>
public enum ProcessFilterSortField
{
    /// <summary>按进程名称排序。</summary>
    ProcessName,
    /// <summary>按进程 ID 排序。</summary>
    ProcessId,
    /// <summary>按启动时间排序。</summary>
    StartedAtUtc
}
