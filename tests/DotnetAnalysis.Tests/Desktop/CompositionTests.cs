using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Diagnostics.DependencyInjection;
using DotnetAnalysis.Diagnostics.Windows;
using DotnetAnalysis.Desktop.Composition;
using DotnetAnalysis.Desktop.Infrastructure;
using DotnetAnalysis.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetAnalysis.Tests.Desktop;

[TestClass]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names describe the behavior under test.")]
public sealed class CompositionTests
{
    [TestMethod]
    public async Task AddDesktopApplication_ResolvesShellAndSingleEventBus()
    {
        var services = new ServiceCollection();
        services.AddDesktopApplication();
        await using var provider = services.BuildServiceProvider(validateScopes: true);

        var shell = provider.GetRequiredService<ShellViewModel>();
        var firstBus = provider.GetRequiredService<IEventBus>();
        var secondBus = provider.GetRequiredService<IEventBus>();

        Assert.IsNotNull(shell);
        Assert.AreSame(firstBus, secondBus);
        Assert.AreEqual("尚未开始分析", shell.StatusText);
    }

    [TestMethod]
    public async Task Composition_ResolvesDiagnosticsOnlyAfterRootRegistration()
    {
        var services = new ServiceCollection();
        services.AddDesktopApplication();
        services.AddWindowsProcessDiagnostics();
        await using var provider = services.BuildServiceProvider(validateScopes: true);

        var eventBus = provider.GetRequiredService<IEventBus>();
        var diagnostics = provider.GetRequiredService<IProcessDiagnostics>();

        Assert.IsNotNull(diagnostics);
        Assert.IsInstanceOfType<WindowsProcessDiagnostics>(diagnostics);
        Assert.AreSame(eventBus, ((WindowsProcessDiagnostics)diagnostics).EventBus);
        Assert.IsNotNull(provider.GetRequiredService<IMemorySnapshotAnalysisService>());
        Assert.IsNotNull(provider.GetRequiredService<ShellViewModel>());
    }

    [TestMethod]
    public async Task DiagnosticsRegistration_RequiresExplicitEventBusRegistration()
    {
        var services = new ServiceCollection();
        services.AddWindowsProcessDiagnostics();
        await using var provider = services.BuildServiceProvider(validateScopes: true);

        Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IProcessDiagnostics>());
    }

    [TestMethod]
    public async Task ShellViewModel_WhenUiDispatchFails_IsolatesHandlerFailure()
    {
        await using var eventBus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        using var shell = new ShellViewModel(
            eventBus,
            new ThrowingUiDispatcher(),
            NullLogger<ShellViewModel>.Instance);

        await eventBus.PublishAsync(
            new MemorySnapshotCaptureStarted(
                DotnetAnalysis.Core.Diagnostics.ProcessDiagnosticsSessionId.New(),
                DateTimeOffset.UtcNow,
                "test"),
            CancellationToken.None);

        await Task.Delay(50);
        Assert.AreEqual("尚未开始分析", shell.StatusText);
    }

    [TestMethod]
    public void DesktopSource_ImportsDiagnosticsOnlyFromCompositionRoot()
    {
        var desktopRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "DotnetAnalysis.Desktop"));

        foreach (var source in Directory.EnumerateFiles(desktopRoot, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(source);
            if (Path.GetFileName(source).Equals("App.xaml.cs", StringComparison.OrdinalIgnoreCase))
            {
                StringAssert.Contains(text, "AddWindowsProcessDiagnostics");
                continue;
            }

            Assert.IsFalse(text.Contains("DotnetAnalysis.Diagnostics", StringComparison.Ordinal));
            Assert.IsFalse(text.Contains("AddWindowsProcessDiagnostics", StringComparison.Ordinal));
        }
    }

    private sealed class ThrowingUiDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("Dispatcher unavailable."));
    }
}
