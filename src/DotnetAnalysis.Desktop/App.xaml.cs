using System.Windows;
using DotnetAnalysis.Diagnostics.DependencyInjection;
using DotnetAnalysis.Desktop.Composition;
using DotnetAnalysis.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetAnalysis.Desktop;

/// <summary>
/// 负责桌面应用启动、依赖注入组装和退出清理。
/// </summary>
public partial class App : System.Windows.Application
{
    private ServiceProvider? _serviceProvider;

    /// <summary>
    /// 构建依赖注入容器并显示主窗口。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        services.AddDesktopApplication();
        services.AddWindowsProcessDiagnostics();
        _serviceProvider = services.BuildServiceProvider(validateScopes: true);

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.DataContext = _serviceProvider.GetRequiredService<ShellViewModel>();
        mainWindow.Show();
    }

    /// <summary>
    /// 释放依赖注入容器及其异步资源。
    /// </summary>
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
