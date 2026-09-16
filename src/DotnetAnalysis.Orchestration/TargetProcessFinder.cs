using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Orchestration.Models;

namespace DotnetAnalysis.Orchestration;

/// <summary>
/// 编排目标进程的快速刷新、去重、筛选和稳定排序。
/// </summary>
public sealed class TargetProcessFinder
{
    private readonly IProcessDiagnostics _diagnostics;
    private readonly TargetCapabilityProbe? _capabilityProbe;

    /// <summary>
    /// 创建目标进程查找器。
    /// </summary>
    /// <param name="diagnostics">提供进程枚举能力的 Application 契约。</param>
    public TargetProcessFinder(
        IProcessDiagnostics diagnostics,
        TargetCapabilityProbe? capabilityProbe = null)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _capabilityProbe = capabilityProbe;
    }

    /// <summary>
    /// 刷新目标候选并应用可在快速枚举阶段完成的筛选和排序。
    /// </summary>
    /// <param name="filter">名称、路径和结果页参数。</param>
    /// <param name="cancellationToken">取消刷新的令牌。</param>
    /// <returns>按目标身份去重后的候选集合。</returns>
    public async Task<IReadOnlyList<TargetProcess>> FindAsync(
        ProcessFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var processes = await _diagnostics.GetProcessesAsync(cancellationToken).ConfigureAwait(false);
        var candidates = processes
            .GroupBy(static process => (process.ProcessId, process.StartedAtUtc))
            .Select(static group => group.First())
            .Where(process => Matches(process, filter))
            .ToArray();

        if (filter.Runtime is not null || filter.Architecture is not null)
        {
            if (_capabilityProbe is null)
            {
                throw new DiagnosticsException(
                    DiagnosticsErrorCode.RuntimeNotSupported,
                    "运行时或架构筛选需要配置能力探测器。");
            }

            var capabilityMatches = new List<TargetProcess>(candidates.Length);
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var context = await _capabilityProbe.ProbeAsync(candidate, cancellationToken).ConfigureAwait(false);
                if (Matches(context, filter))
                {
                    capabilityMatches.Add(candidate);
                }
            }

            candidates = capabilityMatches.ToArray();
        }

        var ordered = filter.SortBy switch
        {
            ProcessFilterSortField.ProcessName => filter.Descending
                ? candidates.OrderByDescending(static process => process.ProcessName, StringComparer.OrdinalIgnoreCase).ThenBy(static process => process.ProcessId)
                : candidates.OrderBy(static process => process.ProcessName, StringComparer.OrdinalIgnoreCase).ThenBy(static process => process.ProcessId),
            ProcessFilterSortField.ProcessId => filter.Descending
                ? candidates.OrderByDescending(static process => process.ProcessId).ThenBy(static process => process.StartedAtUtc)
                : candidates.OrderBy(static process => process.ProcessId).ThenBy(static process => process.StartedAtUtc),
            ProcessFilterSortField.StartedAtUtc => filter.Descending
                ? candidates.OrderByDescending(static process => process.StartedAtUtc).ThenBy(static process => process.ProcessId)
                : candidates.OrderBy(static process => process.StartedAtUtc).ThenBy(static process => process.ProcessId),
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter.SortBy, "Unknown process sort field.")
        };

        return ordered.Take(filter.PageSize).ToArray();
    }

    private static bool Matches(TargetProcess process, ProcessFilter filter)
    {
        if (filter.ProcessName is not null
            && !process.ProcessName.Contains(filter.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return filter.ExecutablePath is null
            || (process.ExecutablePath is not null
                && string.Equals(process.ExecutablePath, filter.ExecutablePath, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Matches(TargetContext context, ProcessFilter filter) =>
        (filter.Runtime is null || string.Equals(context.Runtime, filter.Runtime, StringComparison.OrdinalIgnoreCase))
        && (filter.Architecture is null || context.Architecture == filter.Architecture);
}

