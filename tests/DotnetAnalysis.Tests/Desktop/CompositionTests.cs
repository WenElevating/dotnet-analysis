using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Application.Contracts;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Application.Sessions;
using DotnetAnalysis.Core.Sessions;
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
    public async Task AddDesktopApplication_WithAdapterServices_ResolvesCoordinator()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICaptureBackend, StubCaptureBackend>();
        services.AddSingleton<IAnalysisService, StubAnalysisService>();
        services.AddDesktopApplication();
        await using var provider = services.BuildServiceProvider(validateScopes: true);

        var coordinator = provider.GetRequiredService<AnalysisSessionCoordinator>();
        var timeProvider = provider.GetRequiredService<TimeProvider>();

        Assert.IsNotNull(coordinator);
        Assert.AreSame(TimeProvider.System, timeProvider);
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
        var faultBarrierReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var faultSubscription = eventBus.Subscribe<ModuleFaulted>((@event, _) =>
        {
            if (@event.Source == "barrier")
            {
                faultBarrierReceived.TrySetResult();
            }
            else
            {
                faultReceived.TrySetResult();
            }

            return ValueTask.CompletedTask;
        });

        // Act
        await eventBus.PublishAsync(
            new CaptureStarted(null, DateTimeOffset.UtcNow, "test"),
            CancellationToken.None);
        await dispatcher.FirstInvocation.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await eventBus.PublishAsync(
            new CaptureStarted(null, DateTimeOffset.UtcNow, "barrier"),
            CancellationToken.None);
        await dispatcher.SecondInvocation.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await eventBus.PublishAsync(
            new ModuleFaulted(null, "barrier", "barrier", DateTimeOffset.UtcNow, "barrier"),
            CancellationToken.None);
        await faultBarrierReceived.Task.WaitAsync(TimeSpan.FromSeconds(1));

        // Assert
        Assert.IsFalse(faultReceived.Task.IsCompleted);
        Assert.AreEqual("尚未开始分析", shell.StatusText);
    }

    private sealed class ThrowingUiDispatcher : IUiDispatcher
    {
        private int _invocationCount;

        public TaskCompletionSource FirstInvocation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondInvocation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _invocationCount) == 1)
            {
                FirstInvocation.TrySetResult();
            }
            else
            {
                SecondInvocation.TrySetResult();
            }

            return Task.FromException(new InvalidOperationException("Dispatcher unavailable."));
        }
    }

    private sealed class StubCaptureBackend : ICaptureBackend
    {
        public Task StartAllocationTraceAsync(AnalysisSession session, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task StopAllocationTraceAsync(AnalysisSession session, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task CaptureHeapSnapshotAsync(AnalysisSession session, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task CancelAsync(AnalysisSession session, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StubAnalysisService : IAnalysisService
    {
        public Task AnalyzeAsync(AnalysisSession session, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
