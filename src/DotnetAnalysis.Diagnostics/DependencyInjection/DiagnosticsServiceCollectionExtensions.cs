using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetAnalysis.Diagnostics.DependencyInjection;

public static class DiagnosticsServiceCollectionExtensions
{
    public static IServiceCollection AddWindowsProcessDiagnostics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ProcessEnumerator>();
        services.AddSingleton<ProcessIdentityValidator>();
        services.AddSingleton<RuntimeCapabilitiesResolver>();
        services.AddSingleton<IProcessMemoryReader, ProcessMemoryReader>();
        services.AddSingleton<ImportedSnapshotCatalog>();
        services.AddSingleton<IMemorySnapshotReader, GCDumpSnapshotReader>();
        services.AddSingleton<IMemorySnapshotReader, DumpSnapshotReader>();
        services.AddSingleton<MemorySnapshotReaderRegistry>();
        services.AddSingleton<IMemorySnapshotAnalysisService, DiagnosticsMemorySnapshotAnalysisService>();
        services.AddSingleton<IProcessDiagnostics, WindowsProcessDiagnostics>();
        return services;
    }
}
