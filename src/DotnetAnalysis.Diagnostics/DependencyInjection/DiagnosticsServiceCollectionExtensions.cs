using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Diagnostics.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotnetAnalysis.Diagnostics.DependencyInjection;

/// <summary>
/// 集中注册 Windows 诊断适配器及其应用层契约实现。
/// </summary>
public static class DiagnosticsServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Windows 进程诊断实现及其快照分析依赖。
    /// </summary>
    /// <param name="services">要写入服务注册的集合。</param>
    /// <returns>传入的服务集合，便于继续配置。</returns>
    public static IServiceCollection AddWindowsProcessDiagnostics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<ProcessEnumerator>();
        services.AddSingleton<ProcessIdentityValidator>();
        services.AddSingleton<RuntimeCapabilitiesResolver>();
        services.AddSingleton<IProcessMemoryReader, ProcessMemoryReader>();
        services.AddSingleton<ImportedSnapshotCatalog>();
        services.TryAddSingleton<SnapshotStorageLayout>(_ =>
            new SnapshotStorageLayout(SnapshotStorageLayout.GetDefaultRootDirectory()));
        services.AddSingleton<MemorySnapshotStore>();
        services.AddSingleton<IMemorySnapshotReader, GCDumpSnapshotReader>();
        services.AddSingleton<IMemorySnapshotReader, RetentionHeapSnapshotReader>();
        services.AddSingleton<IMemorySnapshotReader, DumpSnapshotReader>();
        services.AddSingleton<MemorySnapshotReaderRegistry>();
        services.AddSingleton<IMemorySnapshotAnalysisService, DiagnosticsMemorySnapshotAnalysisService>();
        services.AddSingleton<IProcessDiagnostics, WindowsProcessDiagnostics>();
        return services;
    }
}
