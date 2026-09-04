using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using System.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 抽象读取操作系统和目标进程运行时能力。
/// </summary>
public interface IProcessRuntimeInspector
{
    /// <summary>
    /// 当前环境是否为 Windows。
    /// </summary>
    bool IsWindows { get; }
    /// <summary>
    /// 当前操作系统是否为 64 位。
    /// </summary>
    bool Is64BitOperatingSystem { get; }
    /// <summary>
    /// 判断目标进程是否加载 CoreCLR。
    /// </summary>
    bool IsCoreClr(TargetProcess process);
    /// <summary>
    /// 读取目标进程 CoreCLR 主版本；未知时返回零。
    /// </summary>
    int GetRuntimeMajorVersion(TargetProcess process);
}

/// <summary>
/// 通过 Windows 进程模块和文件版本读取运行时能力。
/// </summary>
public sealed class SystemProcessRuntimeInspector : IProcessRuntimeInspector
{
    /// <summary>
    /// 当前环境是否运行在 Windows。
    /// </summary>
    public bool IsWindows => OperatingSystem.IsWindows();
    /// <summary>
    /// 当前系统是否为 64 位。
    /// </summary>
    public bool Is64BitOperatingSystem => Environment.Is64BitOperatingSystem;
    /// <summary>
    /// 检查目标是否加载 System.Private.CoreLib。
    /// </summary>
    public bool IsCoreClr(TargetProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            using var target = Process.GetProcessById(process.ProcessId);
            return target.Modules.Cast<ProcessModule>().Any(module =>
                string.Equals(module.ModuleName, "System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 从 CoreLib 文件版本读取目标运行时主版本。
    /// </summary>
    public int GetRuntimeMajorVersion(TargetProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            using var target = Process.GetProcessById(process.ProcessId);
            var coreLib = target.Modules.Cast<ProcessModule>().FirstOrDefault(module =>
                string.Equals(module.ModuleName, "System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase));
            if (coreLib is null)
            {
                return 0;
            }

            var version = FileVersionInfo.GetVersionInfo(coreLib.FileName).FileVersion;
            return Version.TryParse(version, out var parsed) ? parsed.Major : 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
        {
            return 0;
        }
    }
}

/// <summary>
/// 验证目标满足诊断所需的 Windows、位数和 .NET 版本约束。
/// </summary>
public sealed class RuntimeCapabilitiesResolver
{
    private readonly IProcessRuntimeInspector _inspector;
    private readonly IProcessArchitectureInspector _architectureInspector;

    /// <summary>
    /// 创建运行时能力验证器，可注入系统信息读取器以便测试。
    /// </summary>
    public RuntimeCapabilitiesResolver()
        : this(null, null)
    {
    }

    internal RuntimeCapabilitiesResolver(
        IProcessRuntimeInspector? inspector = null,
        IProcessArchitectureInspector? architectureInspector = null)
    {
        _inspector = inspector ?? new SystemProcessRuntimeInspector();
        _architectureInspector = architectureInspector ?? new ProcessArchitectureInspector();
    }

    /// <summary>
    /// 验证目标是受支持的 64 位 Windows CoreCLR 进程，版本范围为 .NET 8 至 .NET 10。
    /// </summary>
    /// <param name="process">要验证的目标进程。</param>
    /// <param name="cancellationToken">验证前检查的取消令牌。</param>
    /// <exception cref="DiagnosticsException">操作系统、位数、运行时类型或版本不支持时引发。</exception>
    public Task ValidateAsync(TargetProcess process, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_inspector.IsWindows
            || !_inspector.Is64BitOperatingSystem
            || !_architectureInspector.IsAmd64(process)
            || !_inspector.IsCoreClr(process))
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.RuntimeNotSupported, "The target runtime is not supported.");
        }

        var major = _inspector.GetRuntimeMajorVersion(process);
        if (major is < 8 or > 10)
        {
            throw new DiagnosticsException(DiagnosticsErrorCode.RuntimeNotSupported, "The target runtime version is not supported.");
        }

        return Task.CompletedTask;
    }
}
