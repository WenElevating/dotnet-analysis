using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace DotnetAnalysis.Tests.Diagnostics;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class EventPipeExecutionSamplerTests
{
    private static readonly string[] ExpectedRootToLeafFrames = ["Root", "Caller", "Leaf"];

    [TestMethod]
    public async Task StartAsync_UsesSampleProfilerAndPersistsRootToLeafStackWithLostEvents()
    {
        var fixture = CreateFixture();
        var observedAtUtc = fixture.StartedAtUtc.AddSeconds(1);
        var source = new ControlledEventPipeExecutionTraceSource(
            Sample(
                observedAtUtc,
                Frame("Leaf", "leaf"),
                Frame("Caller", "caller"),
                Frame("Root", "root")),
            eventsLost: 7);
        var runtime = new ControlledEventPipeExecutionRuntime(source);
        var sampler = new EventPipeExecutionSampler(fixture.Store, runtime, TimeSpan.FromSeconds(1));

        try
        {
            await sampler.StartAsync(Target(fixture.StartedAtUtc), CancellationToken.None);
            await source.SampleDispatchCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await sampler.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            await sampler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

            Assert.HasCount(2, runtime.Providers);
            var sampleProvider = runtime.Providers.Single(
                provider => provider.Name == SampleProfilerTraceEventParser.ProviderName);
            var runtimeProvider = runtime.Providers.Single(
                provider => provider.Name == ClrTraceEventParser.ProviderName);
            Assert.AreEqual(EventLevel.Informational, sampleProvider.EventLevel);
            Assert.AreEqual(EventLevel.Informational, runtimeProvider.EventLevel);
            Assert.AreEqual((long)ClrTraceEventParser.Keywords.Default, runtimeProvider.Keywords);
            Assert.IsTrue(runtime.RequestRundown);
            Assert.AreEqual(32, runtime.CircularBufferMegabytes);
            Assert.AreEqual(1, source.SampleProfilerThreadSampleSubscriptionCount);
            Assert.AreEqual(1L, sampler.SuccessfulSampleCount);
            Assert.AreEqual(7L, sampler.LostEventCount);

            var boundary = fixture.Store.CaptureReadBoundary();
            var records = await ReadAllAsync(
                fixture.Store,
                new ExecutionTimeRange(fixture.StartedAtUtc, observedAtUtc.AddSeconds(1)),
                boundary);
            Assert.HasCount(1, records);
            var frames = await fixture.Store.GetStackFramesAsync(records[0].StackId, CancellationToken.None);
            CollectionAssert.AreEqual(
                ExpectedRootToLeafFrames,
                frames.Select(frame => frame.Descriptor.MethodName).ToArray());
        }
        finally
        {
            await DisposeSamplerIgnoringFailureAsync(sampler);
            await fixture.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ProcessEvents_WhenStoreWriteFails_ReportsStableStorageFailure()
    {
        var fixture = CreateFixture(
            beforeBoundaryPublishAsync: () => throw new IOException("disk full"));
        var source = new ControlledEventPipeExecutionTraceSource(
            Sample(fixture.StartedAtUtc.AddSeconds(1), Frame("Worker", "worker")));
        var runtime = new ControlledEventPipeExecutionRuntime(source);
        var sampler = new EventPipeExecutionSampler(fixture.Store, runtime, TimeSpan.FromSeconds(1));

        try
        {
            await sampler.StartAsync(Target(fixture.StartedAtUtc), CancellationToken.None);
            await source.ProcessingCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await sampler.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

            Assert.IsNotNull(sampler.TerminalFailure);
            Assert.AreEqual(
                DiagnosticsErrorCode.ExecutionProfileStorageFailed,
                sampler.TerminalFailure.ErrorCode);
            Assert.AreEqual(0L, sampler.SuccessfulSampleCount);
            Assert.IsGreaterThanOrEqualTo(1, source.StopProcessingCallCount);
        }
        finally
        {
            await DisposeSamplerIgnoringFailureAsync(sampler);
            await fixture.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task StopAsync_WhenAppendIsBlocked_ReturnsBoundedWithoutDisposingActiveProcessor()
    {
        var appendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAppend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = CreateFixture(async () =>
        {
            appendStarted.TrySetResult();
            await releaseAppend.Task.ConfigureAwait(false);
        });
        var source = new ControlledEventPipeExecutionTraceSource(
            Sample(fixture.StartedAtUtc.AddSeconds(1), Frame("Worker", "worker")));
        var runtime = new ControlledEventPipeExecutionRuntime(source);
        var sampler = new EventPipeExecutionSampler(fixture.Store, runtime, TimeSpan.FromMilliseconds(50));
        Task? disposeTask = null;

        try
        {
            await sampler.StartAsync(Target(fixture.StartedAtUtc), CancellationToken.None);
            await appendStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var stopwatch = Stopwatch.StartNew();
            await sampler.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            stopwatch.Stop();

            Assert.IsLessThan(TimeSpan.FromSeconds(1), stopwatch.Elapsed);
            Assert.IsNotNull(sampler.TerminalFailure);
            Assert.AreEqual(DiagnosticsErrorCode.ExecutionProfilingUnavailable, sampler.TerminalFailure.ErrorCode);
            Assert.IsFalse(source.ProcessingCompleted.Task.IsCompleted);
            Assert.AreEqual(0, source.DisposeCallCount);
            Assert.AreEqual(0, runtime.Session.DisposeCallCount);

            disposeTask = sampler.DisposeAsync().AsTask();
            await Task.Delay(50);
            Assert.IsFalse(disposeTask.IsCompleted);
            Assert.AreEqual(0, source.DisposeCallCount);
            Assert.AreEqual(0, runtime.Session.DisposeCallCount);

            releaseAppend.TrySetResult();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.AreEqual(1, source.DisposeCallCount);
            Assert.AreEqual(1, runtime.Session.DisposeCallCount);
            Assert.IsFalse(source.WasDisposedWhileProcessing);
        }
        finally
        {
            releaseAppend.TrySetResult();
            if (disposeTask is not null)
            {
                await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                await DisposeSamplerIgnoringFailureAsync(sampler);
            }

            await fixture.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task DisposeAsync_WhenStopFaults_StillStopsProcessorAndDisposesResources()
    {
        var source = new ControlledEventPipeExecutionTraceSource(sample: null);
        var runtime = new ControlledEventPipeExecutionRuntime(source)
        {
            StopException = new NotSupportedException("stop failed")
        };
        var fixture = CreateFixture();
        var sampler = new EventPipeExecutionSampler(fixture.Store, runtime, TimeSpan.FromSeconds(1));

        try
        {
            await sampler.StartAsync(Target(fixture.StartedAtUtc), CancellationToken.None);
            await source.ProcessingStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            await Assert.ThrowsExactlyAsync<NotSupportedException>(async () =>
                await sampler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));

            Assert.IsTrue(source.ProcessingCompleted.Task.IsCompleted);
            Assert.AreEqual(1, source.DisposeCallCount);
            Assert.AreEqual(1, runtime.Session.DisposeCallCount);
            Assert.IsFalse(source.WasDisposedWhileProcessing);
        }
        finally
        {
            await DisposeSamplerIgnoringFailureAsync(sampler);
            await fixture.DisposeAsync();
        }
    }

    private static EventPipeExecutionSample Sample(
        DateTimeOffset observedAtUtc,
        params EventPipeExecutionFrame[] leafToRootFrames) =>
        new(observedAtUtc, ThreadId: 17, leafToRootFrames);

    private static EventPipeExecutionFrame Frame(string methodName, string symbolKey) =>
        new(methodName, "App", "C:\\App.dll", symbolKey, SymbolAddress: null);

    private static TargetProcess Target(DateTimeOffset startedAtUtc) =>
        new(processId: 1234, startedAtUtc.AddMinutes(-1), "target", "C:\\target.exe");

    private static async Task<List<ExecutionSampleRecord>> ReadAllAsync(
        ExecutionCaptureStore store,
        ExecutionTimeRange range,
        ExecutionCaptureReadBoundary boundary)
    {
        var records = new List<ExecutionSampleRecord>();
        await foreach (var record in store.ReadAsync(range, boundary, CancellationToken.None))
        {
            records.Add(record);
        }

        return records;
    }

    private static SamplerFixture CreateFixture(Func<Task>? beforeBoundaryPublishAsync = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new ExecutionCaptureStore(
            new ExecutionCaptureStorageLayout(root),
            beforeBoundaryPublishAsync: beforeBoundaryPublishAsync);
        return new SamplerFixture(root, store, store.CaptureReadBoundary().StartedAtUtc);
    }

    private static async Task DisposeSamplerIgnoringFailureAsync(EventPipeExecutionSampler sampler)
    {
        try
        {
            await sampler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (NotSupportedException)
        {
        }
    }

    private sealed class ControlledEventPipeExecutionRuntime : IEventPipeExecutionRuntime
    {
        private readonly ControlledEventPipeExecutionTraceSource _source;

        public ControlledEventPipeExecutionRuntime(ControlledEventPipeExecutionTraceSource source)
        {
            _source = source;
            Session = new ControlledEventPipeExecutionSession(source);
        }

        public IReadOnlyList<EventPipeProvider> Providers { get; private set; } = [];

        public bool RequestRundown { get; private set; }

        public int CircularBufferMegabytes { get; private set; }

        public Exception? StopException
        {
            get => Session.StopException;
            init => Session.StopException = value;
        }

        public ControlledEventPipeExecutionSession Session { get; }

        public IEventPipeExecutionSession StartSession(
            int processId,
            IReadOnlyCollection<EventPipeProvider> providers,
            bool requestRundown,
            int circularBufferMegabytes)
        {
            _ = processId;
            Providers = providers.ToArray();
            RequestRundown = requestRundown;
            CircularBufferMegabytes = circularBufferMegabytes;
            return Session;
        }

        public IEventPipeExecutionTraceSource CreateTraceSource(IEventPipeExecutionSession session)
        {
            Assert.AreSame(Session, session);
            return _source;
        }
    }

    private sealed class ControlledEventPipeExecutionSession : IEventPipeExecutionSession
    {
        private readonly ControlledEventPipeExecutionTraceSource _source;

        public ControlledEventPipeExecutionSession(ControlledEventPipeExecutionTraceSource source)
        {
            _source = source;
        }

        public Exception? StopException { get; set; }

        public int DisposeCallCount { get; private set; }

        public void Stop()
        {
            if (StopException is not null)
            {
                throw StopException;
            }

            _source.CompleteInput();
        }

        public void Dispose() => DisposeCallCount++;
    }

    private sealed class ControlledEventPipeExecutionTraceSource : IEventPipeExecutionTraceSource
    {
        private readonly EventPipeExecutionSample? _sample;
        private readonly ManualResetEventSlim _inputCompleted = new(initialState: false);
        private Action<EventPipeExecutionSample>? _sampleObserved;

        public ControlledEventPipeExecutionTraceSource(
            EventPipeExecutionSample? sample,
            long eventsLost = 0)
        {
            _sample = sample;
            EventsLost = eventsLost;
        }

        public TaskCompletionSource ProcessingStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SampleDispatchCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ProcessingCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long EventsLost { get; }

        public int SampleProfilerThreadSampleSubscriptionCount { get; private set; }

        public int StopProcessingCallCount { get; private set; }

        public int DisposeCallCount { get; private set; }

        public bool WasDisposedWhileProcessing { get; private set; }

        public void SubscribeSampleProfilerThreadSample(Action<EventPipeExecutionSample> sampleObserved)
        {
            _sampleObserved = sampleObserved;
            SampleProfilerThreadSampleSubscriptionCount++;
        }

        public void Process()
        {
            ProcessingStarted.TrySetResult();
            try
            {
                if (_sample is not null)
                {
                    _sampleObserved!(_sample);
                }

                SampleDispatchCompleted.TrySetResult();
                _inputCompleted.Wait();
            }
            finally
            {
                ProcessingCompleted.TrySetResult();
            }
        }

        public void StopProcessing()
        {
            StopProcessingCallCount++;
            CompleteInput();
        }

        public void CompleteInput() => _inputCompleted.Set();

        public void Dispose()
        {
            WasDisposedWhileProcessing = !ProcessingCompleted.Task.IsCompleted;
            DisposeCallCount++;
            _inputCompleted.Dispose();
        }
    }

    private sealed class SamplerFixture : IAsyncDisposable
    {
        private readonly string _root;

        public SamplerFixture(string root, ExecutionCaptureStore store, DateTimeOffset startedAtUtc)
        {
            _root = root;
            Store = store;
            StartedAtUtc = startedAtUtc;
        }

        public ExecutionCaptureStore Store { get; }

        public DateTimeOffset StartedAtUtc { get; }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
