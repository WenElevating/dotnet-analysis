using DotnetAnalysis.Application.Contracts.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotnetAnalysis.Orchestration.DependencyInjection;

/// <summary>提供编排层自包含的依赖注入注册入口。</summary>
public static class OrchestrationServiceCollectionExtensions
{
    /// <summary>注册编排层自身类型；底层诊断、分析服务和事件总线由宿主提供。</summary>
    /// <param name="services">要写入注册的服务集合。</param>
    /// <returns>传入的服务集合，便于继续配置。</returns>
    public static IServiceCollection AddOrchestration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<TargetCapabilityProbe>();
        services.TryAddSingleton<TargetProcessFinder>();
        services.TryAddSingleton<IDiagnosticsApplication, DiagnosticsApplication>();
        return services;
    }
}