using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Application.Events;
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
        // Arrange
        var services = new ServiceCollection();
        services.AddDesktopApplication();
        await using var provider = services.BuildServiceProvider(validateScopes: true);

        // Act
        var shell = provider.GetRequiredService<ShellViewModel>();
        var firstBus = provider.GetRequiredService<IEventBus>();
        var secondBus = provider.GetRequiredService<IEventBus>();

        // Assert
        Assert.IsNotNull(shell);
        Assert.AreSame(firstBus, secondBus);
        Assert.AreEqual("尚未开始分析", shell.StatusText);
    }

    [TestMethod]
    public async Task ShellViewModel_WhenUiDispatchFails_ContainsHandlerFailure()
    {
        // Arrange
        await using var eventBus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var dispatcher = new ThrowingUiDispatcher();
        using var shell = new ShellViewModel(
            eventBus,
            dispatcher,
            NullLogger<ShellViewModel>.Instance);
        var faultReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var faultSubscription = eventBus.Subscribe<ModuleFaulted>((_, _) =>
        {
            faultReceived.TrySetResult();
            return ValueTask.CompletedTask;
        });

        // Act
        await eventBus.PublishAsync(
            new CaptureStarted(null, DateTimeOffset.UtcNow, "test"),
            CancellationToken.None);
        await dispatcher.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(1));

        // Assert
        await Task.Delay(50);
        Assert.IsFalse(faultReceived.Task.IsCompleted);
        Assert.AreEqual("尚未开始分析", shell.StatusText);
    }

    private sealed class ThrowingUiDispatcher : IUiDispatcher
    {
        public TaskCompletionSource Invoked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            Invoked.TrySetResult();
            return Task.FromException(new InvalidOperationException("Dispatcher unavailable."));
        }
    }
}
