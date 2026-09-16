using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Orchestration.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetAnalysis.Orchestration.Tests;

/// <summary>验证编排层可在不加载 Desktop/WPF 的独立宿主中完成注册和解析。</summary>
[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe orchestration behavior.")]
public sealed class OrchestrationCompositionTests
{
    [TestMethod]
    public async Task AddOrchestration_ResolvesApplicationWithoutDesktopDependency()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProcessDiagnostics, CompositionDiagnostics>();
        services.AddSingleton<ITargetProcessLauncher, CompositionLauncher>();
        services.AddSingleton<IMemorySnapshotAnalysisService, CompositionAnalysisService>();
        services.AddSingleton<IEventBus, CompositionEventBus>();
        services.AddOrchestration();

        await using var provider = services.BuildServiceProvider();
        var application = provider.GetRequiredService<IDiagnosticsApplication>();

        Assert.IsInstanceOfType<DiagnosticsApplication>(application);
        Assert.IsNotNull(provider.GetRequiredService<TargetProcessFinder>());
        Assert.IsNotNull(provider.GetRequiredService<TargetCapabilityProbe>());
        Assert.IsFalse(application.GetType().Assembly.GetName().Name!.Contains("Desktop", StringComparison.OrdinalIgnoreCase));
        await application.CloseAsync(CancellationToken.None);
    }

    private sealed class CompositionDiagnostics : IProcessDiagnostics
    {
        public Task<IReadOnlyList<TargetProcess>> GetProcessesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TargetProcess>>([]);
        public Task<TargetProcessCapabilities> ProbeCapabilitiesAsync(TargetProcess process, CancellationToken cancellationToken) => Task.FromException<TargetProcessCapabilities>(new NotSupportedException());
        public Task<IProcessDiagnosticsSession> AttachAsync(TargetProcess process, CancellationToken cancellationToken) => Task.FromException<IProcessDiagnosticsSession>(new NotSupportedException());
        public Task<MemorySnapshot> OpenSnapshotAsync(string filePath, CancellationToken cancellationToken) => Task.FromException<MemorySnapshot>(new NotSupportedException());
    }

    private sealed class CompositionLauncher : ITargetProcessLauncher
    {
        public Task<TargetProcessLaunchResult> LaunchAsync(TargetProcessLaunchRequest request, CancellationToken cancellationToken) => Task.FromException<TargetProcessLaunchResult>(new NotSupportedException());
    }

    private sealed class CompositionAnalysisService : IMemorySnapshotAnalysisService
    {
        public Task<MemorySnapshotAnalysis> AnalyzeAsync(MemorySnapshot snapshot, CancellationToken cancellationToken) => Task.FromException<MemorySnapshotAnalysis>(new NotSupportedException());
        public Task<IReadOnlyList<MemoryObjectInfo>> GetObjectsAsync(MemorySnapshot snapshot, TypeIdentity type, CancellationToken cancellationToken) => Task.FromException<IReadOnlyList<MemoryObjectInfo>>(new NotSupportedException());
        public Task<MemoryObjectPage> GetObjectsPageAsync(MemorySnapshot snapshot, TypeIdentity type, int offset, int pageSize, CancellationToken cancellationToken) => Task.FromException<MemoryObjectPage>(new NotSupportedException());
        public Task<MemoryReferencePath?> GetReferencePathAsync(MemorySnapshot snapshot, ulong objectAddress, CancellationToken cancellationToken) => Task.FromException<MemoryReferencePath?>(new NotSupportedException());
    }

    private sealed class CompositionEventBus : IEventBus
    {
        public ValueTask PublishAsync<TEvent>(TEvent applicationEvent, CancellationToken cancellationToken) where TEvent : DotnetAnalysis.Core.Events.IApplicationEvent => ValueTask.CompletedTask;
        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler, EventSubscriptionOptions? options = null) where TEvent : DotnetAnalysis.Core.Events.IApplicationEvent => new NoopDisposable();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}
