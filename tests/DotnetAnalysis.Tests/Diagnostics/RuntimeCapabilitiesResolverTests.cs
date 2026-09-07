using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;
using DotnetAnalysis.Diagnostics.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetAnalysis.Tests.Diagnostics;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class RuntimeCapabilitiesResolverTests
{
    [TestMethod]
    public async Task ValidateAsync_WhenTargetIsNotAmd64_RejectsRuntime()
    {
        var resolver = new RuntimeCapabilitiesResolver(
            new SupportedRuntimeInspector(),
            new UnsupportedArchitectureInspector());

        var exception = await Assert.ThrowsAsync<DiagnosticsException>(
            async () => await resolver.ValidateAsync(Target(), CancellationToken.None));

        Assert.AreEqual(DiagnosticsErrorCode.RuntimeNotSupported, exception.ErrorCode);
    }

    [TestMethod]
    public async Task AttachAsync_StartsExecutionSamplingAndRetainsItsUnavailableState()
    {
        var process = Target();
        var executionSampling = new UnavailableOnStartExecutionSamplingSession();
        var diagnostics = new WindowsProcessDiagnostics(
            new RecordingEventBus(),
            TimeProvider.System,
            () => executionSampling,
            NullLogger<ProcessDiagnosticsSession>.Instance,
            identityValidator: new ProcessIdentityValidator(new FixedProcessIdentitySource(process.StartedAtUtc)),
            capabilitiesResolver: new RuntimeCapabilitiesResolver(
                new SupportedRuntimeInspector(),
                new SupportedArchitectureInspector()),
            processMemoryReader: new UnavailableProcessMemoryReader());

        await using var session = await diagnostics.AttachAsync(process, CancellationToken.None);

        Assert.AreEqual(1, executionSampling.StartCallCount);
        Assert.AreEqual(process, executionSampling.StartedTarget);
        Assert.AreEqual(ProcessDiagnosticsSessionState.Monitoring, session.State);
        var exception = await Assert.ThrowsAsync<DiagnosticsException>(
            async () => await session.GetExecutionProfileAsync(TimeRange(), CancellationToken.None));
        Assert.AreEqual(DiagnosticsErrorCode.ExecutionProfilingUnavailable, exception.ErrorCode);
    }

    private static TargetProcess Target() => new(
        42,
        DateTimeOffset.Parse("2026-09-04T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        "test",
        "C:\\test.exe");

    private sealed class SupportedRuntimeInspector : IProcessRuntimeInspector
    {
        public bool IsWindows => true;

        public bool Is64BitOperatingSystem => true;

        public bool IsCoreClr(TargetProcess process) => true;

        public int GetRuntimeMajorVersion(TargetProcess process) => 10;
    }

    private sealed class UnsupportedArchitectureInspector : IProcessArchitectureInspector
    {
        public bool IsAmd64(TargetProcess process) => false;
    }

    private sealed class SupportedArchitectureInspector : IProcessArchitectureInspector
    {
        public bool IsAmd64(TargetProcess process) => true;
    }

    private sealed class FixedProcessIdentitySource : IProcessIdentitySource
    {
        private readonly DateTimeOffset _startedAtUtc;

        public FixedProcessIdentitySource(DateTimeOffset startedAtUtc)
        {
            _startedAtUtc = startedAtUtc;
        }

        public DateTimeOffset GetStartedAtUtc(int processId) => _startedAtUtc;
    }

    private sealed class UnavailableProcessMemoryReader : IProcessMemoryReader
    {
        public long? ReadPrivateWorkingSetBytes(int processId) => null;
    }

    private sealed class UnavailableOnStartExecutionSamplingSession : IExecutionSamplingSession
    {
        public int StartCallCount { get; private set; }

        public TargetProcess? StartedTarget { get; private set; }

        public Task StartAsync(TargetProcess target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartedTarget = target;
            StartCallCount++;
            return Task.FromException(
                new DiagnosticsException(
                    DiagnosticsErrorCode.ExecutionProfilingUnavailable,
                    "Execution sampling could not start."));
        }

        public Task<ExecutionProfile> GetExecutionProfileAsync(
            ExecutionTimeRange timeRange,
            CancellationToken cancellationToken) =>
            Task.FromException<ExecutionProfile>(
                new DiagnosticsException(
                    DiagnosticsErrorCode.ExecutionProfilingUnavailable,
                    "Execution sampling is unavailable."));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingEventBus : IEventBus
    {
        public ValueTask PublishAsync<TEvent>(TEvent applicationEvent, CancellationToken cancellationToken)
            where TEvent : IApplicationEvent => ValueTask.CompletedTask;

        public IDisposable Subscribe<TEvent>(
            Func<TEvent, CancellationToken, ValueTask> handler,
            EventSubscriptionOptions? options = null)
            where TEvent : IApplicationEvent => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ExecutionTimeRange TimeRange() =>
        new(
            DateTimeOffset.Parse("2026-09-02T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-09-02T08:01:00Z", System.Globalization.CultureInfo.InvariantCulture));
}
