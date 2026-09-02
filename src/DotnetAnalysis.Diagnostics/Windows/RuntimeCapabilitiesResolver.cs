using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

public interface IProcessRuntimeInspector
{
    bool IsWindows { get; }
    bool Is64BitOperatingSystem { get; }
    bool IsCoreClr(TargetProcess process);
    int GetRuntimeMajorVersion(TargetProcess process);
}

public sealed class SystemProcessRuntimeInspector : IProcessRuntimeInspector
{
    public bool IsWindows => OperatingSystem.IsWindows();
    public bool Is64BitOperatingSystem => Environment.Is64BitOperatingSystem;
    public bool IsCoreClr(TargetProcess process) => true;
    public int GetRuntimeMajorVersion(TargetProcess process) => 10;
}

public sealed class RuntimeCapabilitiesResolver
{
    private readonly IProcessRuntimeInspector _inspector;

    public RuntimeCapabilitiesResolver(IProcessRuntimeInspector? inspector = null)
    {
        _inspector = inspector ?? new SystemProcessRuntimeInspector();
    }

    public Task ValidateAsync(TargetProcess process, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_inspector.IsWindows || !_inspector.Is64BitOperatingSystem || !_inspector.IsCoreClr(process))
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
