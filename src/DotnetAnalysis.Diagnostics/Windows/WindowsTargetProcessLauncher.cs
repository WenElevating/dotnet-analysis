using System.Diagnostics;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 在 Windows 上启动目标 EXE 并读取其稳定进程身份。
/// </summary>
public sealed class WindowsTargetProcessLauncher : ITargetProcessLauncher
{
    private readonly TimeProvider _timeProvider;

    /// <summary>创建 Windows 目标启动器。</summary>
    /// <param name="timeProvider">提供可测试时间的时钟。</param>
    public WindowsTargetProcessLauncher(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<TargetProcessLaunchResult> LaunchAsync(
        TargetProcessLaunchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = request.ExecutablePath,
                Arguments = request.Arguments ?? string.Empty,
                WorkingDirectory = request.WorkingDirectory ?? string.Empty,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                throw new DiagnosticsException(DiagnosticsErrorCode.CaptureFailed, "无法创建目标进程。");
            }

            var deadline = _timeProvider.GetUtcNow() + request.StartupTimeout;
            while (_timeProvider.GetUtcNow() < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var startedAtUtc = process.StartTime.ToUniversalTime();
                    var target = new TargetProcess(process.Id, startedAtUtc, process.ProcessName, request.ExecutablePath);
                    return new TargetProcessLaunchResult(target, identityValidated: true);
                }
                catch (InvalidOperationException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), _timeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), _timeProvider, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new DiagnosticsException(
                DiagnosticsErrorCode.TargetExited,
                "在启动等待截止时间内无法读取目标进程身份。");
        }
        catch
        {
            if (process is not null && request.FailureStrategy is TargetProcessLaunchFailureStrategy.TerminateProcess)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException
                        or System.ComponentModel.Win32Exception
                        or NotSupportedException)
                {
                }
            }

            throw;
        }
        finally
        {
            process?.Dispose();
        }
    }
}
