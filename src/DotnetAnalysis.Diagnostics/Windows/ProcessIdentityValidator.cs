using System.Diagnostics;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 抽象读取进程启动时间的来源，便于隔离系统 API。
/// </summary>
public interface IProcessIdentitySource
{
    /// <summary>
    /// 读取指定进程的 UTC 启动时间。
    /// </summary>
    DateTimeOffset GetStartedAtUtc(int processId);
}

/// <summary>
/// 通过系统进程 API 读取进程身份信息。
/// </summary>
public sealed class SystemProcessIdentitySource : IProcessIdentitySource
{
    /// <summary>
    /// 通过系统进程 API 读取启动时间。
    /// </summary>
    public DateTimeOffset GetStartedAtUtc(int processId)
    {
        using var process = Process.GetProcessById(processId);
        return process.StartTime.ToUniversalTime();
    }
}

/// <summary>
/// 校验目标 PID 与启动时间，防止进程重启或 PID 复用。
/// </summary>
public sealed class ProcessIdentityValidator
{
    private readonly IProcessIdentitySource _source;

    /// <summary>
    /// 创建可验证目标 PID 与启动时间是否仍匹配的验证器。
    /// </summary>
    public ProcessIdentityValidator(IProcessIdentitySource? source = null)
    {
        _source = source ?? new SystemProcessIdentitySource();
    }

    /// <summary>
    /// 验证目标仍是附着时记录的同一进程实例。
    /// </summary>
    /// <param name="target">附着时保存的目标进程身份。</param>
    /// <param name="cancellationToken">验证前检查的取消令牌。</param>
    /// <exception cref="DiagnosticsException">进程退出或 PID 已复用时引发稳定错误码。</exception>
    public Task ValidateAsync(TargetProcess target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var actual = _source.GetStartedAtUtc(target.ProcessId);
            if (actual != target.StartedAtUtc)
            {
                throw new DiagnosticsException(
                    DiagnosticsErrorCode.TargetChanged,
                    "The target process start time changed.");
            }
        }
        catch (DiagnosticsException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.TargetExited,
                "The target process is no longer available.",
                exception);
        }

        return Task.CompletedTask;
    }
}
