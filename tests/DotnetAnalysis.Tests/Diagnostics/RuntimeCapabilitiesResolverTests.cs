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
        var disposalOrder = new List<string>();
        var executionSampling = new ControlledExecutionSamplingSession(disposalOrder)
        {
            StartFailure = new DiagnosticsException(
                DiagnosticsErrorCode.ExecutionProfilingUnavailable,
                "Execution sampling could not start.")
        };
        var allocationSampling = new ControlledAllocationSamplingSessionResource(disposalOrder);
        var memoryReader = new RecordingManagedHeapReader(disposalOrder);
        var diagnostics = CreateDiagnostics(
            () => executionSampling,
            allocationSampling,
            memoryReader);

        await using var session = await diagnostics.AttachAsync(process, CancellationToken.None);

        Assert.AreEqual(1, allocationSampling.StartCallCount);
        Assert.AreEqual(1, executionSampling.StartCallCount);
        Assert.AreEqual(process, executionSampling.StartedTarget);
        Assert.AreEqual(ProcessDiagnosticsSessionState.Monitoring, session.State);
        var exception = await Assert.ThrowsAsync<DiagnosticsException>(
            async () => await session.GetExecutionProfileAsync(TimeRange(), CancellationToken.None));
        Assert.AreEqual(DiagnosticsErrorCode.ExecutionProfilingUnavailable, exception.ErrorCode);

        await session.EndAsync(CancellationToken.None);
        AssertDisposalOrder(disposalOrder, "execution", "allocation", "memory");
    }

    [TestMethod]
    public async Task AttachAsync_WhenExecutionFactoryFails_CleansStartedResourcesAndPreservesPrimaryFailure()
    {
        var disposalOrder = new List<string>();
        var primaryFailure = new InvalidOperationException("Execution session construction failed.");
        var allocationSampling = new ControlledAllocationSamplingSessionResource(disposalOrder)
        {
            DisposeFailure = new IOException("Allocation cleanup failed.")
        };
        var memoryReader = new RecordingManagedHeapReader(disposalOrder)
        {
            DisposeFailure = new IOException("Memory cleanup failed.")
        };
        var diagnostics = CreateDiagnostics(
            () => throw primaryFailure,
            allocationSampling,
            memoryReader);

        var exception = await CaptureExceptionAsync(
            () => diagnostics.AttachAsync(Target(), CancellationToken.None));

        Assert.AreSame(primaryFailure, exception);
        Assert.AreEqual(1, allocationSampling.StartCallCount);
        Assert.IsTrue(allocationSampling.IsDisposed);
        Assert.IsTrue(memoryReader.IsDisposed);
        AssertDisposalOrder(disposalOrder, "allocation", "memory");
    }

    [TestMethod]
    [DataRow("storage")]
    [DataRow("cancellation")]
    [DataRow("unknown")]
    public async Task AttachAsync_WhenExecutionStartFails_CleansAllResourcesAndPreservesPrimaryFailure(
        string failureKind)
    {
        var disposalOrder = new List<string>();
        var primaryFailure = CreateStartFailure(failureKind);
        var executionSampling = new ControlledExecutionSamplingSession(disposalOrder)
        {
            StartFailure = primaryFailure,
            DisposeFailure = new IOException("Execution cleanup failed.")
        };
        var allocationSampling = new ControlledAllocationSamplingSessionResource(disposalOrder)
        {
            DisposeFailure = new IOException("Allocation cleanup failed.")
        };
        var memoryReader = new RecordingManagedHeapReader(disposalOrder)
        {
            DisposeFailure = new IOException("Memory cleanup failed.")
        };
        var diagnostics = CreateDiagnostics(
            () => executionSampling,
            allocationSampling,
            memoryReader);

        var exception = await CaptureExceptionAsync(
            () => diagnostics.AttachAsync(Target(), CancellationToken.None));

        Assert.AreSame(primaryFailure, exception);
        Assert.AreEqual(1, allocationSampling.StartCallCount);
        Assert.IsTrue(executionSampling.IsDisposed);
        Assert.IsTrue(allocationSampling.IsDisposed);
        Assert.IsTrue(memoryReader.IsDisposed);
        AssertDisposalOrder(disposalOrder, "execution", "allocation", "memory");
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

    private static WindowsProcessDiagnostics CreateDiagnostics(
        Func<IExecutionSamplingSession> executionSamplingFactory,
        IAllocationSamplingSessionResource allocationSampling,
        RecordingManagedHeapReader memoryReader) =>
        new(
            new RecordingEventBus(),
            TimeProvider.System,
            executionSamplingFactory,
            NullLogger<ProcessDiagnosticsSession>.Instance,
            identityValidator: new ProcessIdentityValidator(new FixedProcessIdentitySource(Target().StartedAtUtc)),
            capabilitiesResolver: new RuntimeCapabilitiesResolver(
                new SupportedRuntimeInspector(),
                new SupportedArchitectureInspector()),
            processMemoryReader: new UnavailableProcessMemoryReader(),
            allocationSamplingSessionFactory: _ => allocationSampling,
            processMemorySamplerFactory: process => new ProcessMemorySampler(
                process,
                new UnavailableProcessMemoryReader(),
                memoryReader,
                TimeProvider.System,
                TimeSpan.Zero));

    private static Exception CreateStartFailure(string failureKind) => failureKind switch
    {
        "storage" => new DiagnosticsException(
            DiagnosticsErrorCode.ExecutionProfileStorageFailed,
            "Execution storage failed."),
        "cancellation" => new OperationCanceledException("Execution start was cancelled."),
        "unknown" => new InvalidOperationException("Execution start failed."),
        _ => throw new ArgumentOutOfRangeException(nameof(failureKind), failureKind, null)
    };

    private static async Task<Exception> CaptureExceptionAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return exception;
        }

        Assert.Fail("Expected the operation to fail.");
        throw new InvalidOperationException("Assert.Fail should have interrupted the test.");
    }

    private static void AssertDisposalOrder(List<string> actual, params string[] expected)
    {
        Assert.HasCount(expected.Length, actual);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.AreEqual(expected[index], actual[index]);
        }
    }

    private sealed class UnavailableProcessMemoryReader : IProcessMemoryReader
    {
        public long? ReadPrivateWorkingSetBytes(int processId) => null;
    }

    private sealed class ControlledExecutionSamplingSession : IExecutionSamplingSession
    {
        private readonly List<string> _disposalOrder;

        public ControlledExecutionSamplingSession(List<string> disposalOrder)
        {
            _disposalOrder = disposalOrder;
        }

        public int StartCallCount { get; private set; }

        public TargetProcess? StartedTarget { get; private set; }

        public Exception? StartFailure { get; init; }

        public Exception? DisposeFailure { get; init; }

        public bool IsDisposed { get; private set; }

        public Task StartAsync(TargetProcess target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartedTarget = target;
            StartCallCount++;
            return StartFailure is null
                ? Task.CompletedTask
                : Task.FromException(StartFailure);
        }

        public Task<ExecutionProfile> GetExecutionProfileAsync(
            ExecutionTimeRange timeRange,
            CancellationToken cancellationToken) =>
            Task.FromException<ExecutionProfile>(
                new DiagnosticsException(
                    DiagnosticsErrorCode.ExecutionProfilingUnavailable,
                    "Execution sampling is unavailable."));

        public ValueTask DisposeAsync()
        {
            _disposalOrder.Add("execution");
            IsDisposed = true;
            return DisposeFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeFailure);
        }
    }

    private sealed class ControlledAllocationSamplingSessionResource : IAllocationSamplingSessionResource
    {
        private readonly List<string> _disposalOrder;

        public ControlledAllocationSamplingSessionResource(List<string> disposalOrder)
        {
            _disposalOrder = disposalOrder;
            Collector = new AllocationSamplingSession(
                new AllocationProfileBuilder(Target().StartedAtUtc));
        }

        public AllocationSamplingSession Collector { get; }

        public int StartCallCount { get; private set; }

        public Exception? DisposeFailure { get; init; }

        public bool IsDisposed { get; private set; }

        public Task StartAsync(TargetProcess target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCallCount++;
            return Task.CompletedTask;
        }

        public void MarkInterrupted(DateTimeOffset observedAtUtc)
        {
        }

        public async ValueTask DisposeAsync()
        {
            _disposalOrder.Add("allocation");
            IsDisposed = true;
            await Collector.DisposeAsync().ConfigureAwait(false);
            if (DisposeFailure is not null)
            {
                throw DisposeFailure;
            }
        }
    }

    private sealed class RecordingManagedHeapReader : IManagedHeapReader, IDisposable
    {
        private readonly List<string> _disposalOrder;

        public RecordingManagedHeapReader(List<string> disposalOrder)
        {
            _disposalOrder = disposalOrder;
        }

        public Exception? DisposeFailure { get; init; }

        public bool IsDisposed { get; private set; }

        public long? ReadManagedHeapBytes(int processId) => null;

        public void Dispose()
        {
            _disposalOrder.Add("memory");
            IsDisposed = true;
            if (DisposeFailure is not null)
            {
                throw DisposeFailure;
            }
        }
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
