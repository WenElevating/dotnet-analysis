using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Application.Sessions;
using DotnetAnalysis.Desktop.Infrastructure;
using DotnetAnalysis.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetAnalysis.Desktop.Composition;

public static class DesktopServiceCollectionExtensions
{
    public static IServiceCollection AddDesktopApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddLogging();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IEventBus, InProcessEventBus>();
        services.AddSingleton<AnalysisSessionCoordinator>();
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();

        return services;
    }
}
