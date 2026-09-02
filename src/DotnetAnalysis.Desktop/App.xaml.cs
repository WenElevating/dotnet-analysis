using System.Windows;
using DotnetAnalysis.Desktop.Composition;
using DotnetAnalysis.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetAnalysis.Desktop;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        services.AddDesktopApplication();
        _serviceProvider = services.BuildServiceProvider(validateScopes: true);

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.DataContext = _serviceProvider.GetRequiredService<ShellViewModel>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_serviceProvider is not null)
        {
            _serviceProvider.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _serviceProvider = null;
        }

        base.OnExit(e);
    }
}
