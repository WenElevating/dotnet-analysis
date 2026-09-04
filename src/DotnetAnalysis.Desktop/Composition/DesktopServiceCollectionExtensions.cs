using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Desktop.Infrastructure;
using DotnetAnalysis.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetAnalysis.Desktop.Composition;

/// <summary>
/// 集中注册桌面壳层使用的应用服务和基础设施。
/// </summary>
public static class DesktopServiceCollectionExtensions
{
    /// <summary>
    /// 注册桌面应用所需的日志、事件总线、UI 调度器和主窗口服务。
    /// </summary>
    /// <param name="services">要写入服务注册的集合。</param>
    /// <returns>传入的服务集合，便于继续配置。</returns>
    public static IServiceCollection AddDesktopApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddLogging();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IEventBus, InProcessEventBus>();
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();

        return services;
    }
}
