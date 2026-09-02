using System.Diagnostics;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

public interface IProcessIdentitySource
{
    DateTimeOffset GetStartedAtUtc(int processId);
}

public sealed class SystemProcessIdentitySource : IProcessIdentitySource
{
    public DateTimeOffset GetStartedAtUtc(int processId)
    {
        using var process = Process.GetProcessById(processId);
        return process.StartTime.ToUniversalTime();
    }
}

public sealed class ProcessIdentityValidator
{
    private readonly IProcessIdentitySource _source;

    public ProcessIdentityValidator(IProcessIdentitySource? source = null)
    {
        _source = source ?? new SystemProcessIdentitySource();
    }

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
