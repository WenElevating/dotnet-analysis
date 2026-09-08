using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime.ExceptionServices;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace DotnetAnalysis.Diagnostics.Windows;

internal interface IEventPipeExecutionSampler : IAsyncDisposable
{
    long SuccessfulSampleCount { get; }

    long LostEventCount { get; }

    DiagnosticsException? TerminalFailure { get; }

    Task StartAsync(TargetProcess target, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

internal readonly record struct EventPipeExecutionFrame(
    string? MethodName,
    string? ModuleName,
    string? ModulePath,
    string SymbolKey,
    TraceCodeAddress? SymbolAddress);

internal readonly record struct EventPipeExecutionSample(
    DateTimeOffset ObservedAtUtc,
    int ThreadId,
    IReadOnlyList<EventPipeExecutionFrame> LeafToRootFrames);

internal interface IEventPipeExecutionRuntime
{
    IEventPipeExecutionSession StartSession(
        int processId,
        IReadOnlyCollection<EventPipeProvider> providers,
        bool requestRundown,
        int circularBufferMegabytes);

    IEventPipeExecutionTraceSource CreateTraceSource(IEventPipeExecutionSession session);
}

internal interface IEventPipeExecutionSession : IDisposable
{
    void Stop();
}

internal interface IEventPipeExecutionTraceSource : IDisposable
{
    long EventsLost { get; }

    void SubscribeSampleProfilerThreadSample(Action<EventPipeExecutionSample> sampleObserved);

    void Process();

    void StopProcessing();
}

internal sealed class DiagnosticsClientExecutionSamplingRuntime : IEventPipeExecutionRuntime
{
    public IEventPipeExecutionSession StartSession(
        int processId,
        IReadOnlyCollection<EventPipeProvider> providers,
        bool requestRundown,
        int circularBufferMegabytes)
    {
        var client = new DiagnosticsClient(processId);
        var session = client.StartEventPipeSession(
            providers,
            requestRundown,
            circularBufferMegabytes);
        return new DiagnosticsClientExecutionSamplingSession(client, session);
    }

    public IEventPipeExecutionTraceSource CreateTraceSource(IEventPipeExecutionSession session)
    {
        if (session is not DiagnosticsClientExecutionSamplingSession diagnosticsSession)
        {
            throw new ArgumentException("The execution session was not created by this runtime.", nameof(session));
        }

        var source = TraceLog.CreateFromEventPipeSession(
            diagnosticsSession.Session,
            TraceLog.EventPipeRundownConfiguration.Enable(diagnosticsSession.Client));
        return new TraceEventExecutionTraceSource(source);
    }

    private sealed class DiagnosticsClientExecutionSamplingSession : IEventPipeExecutionSession
    {
        public DiagnosticsClientExecutionSamplingSession(DiagnosticsClient client, EventPipeSession session)
        {
            Client = client;
            Session = session;
        }

        public DiagnosticsClient Client { get; }

        public EventPipeSession Session { get; }

        public void Stop() => Session.Stop();

        public void Dispose() => Session.Dispose();
    }

    internal sealed class TraceEventExecutionTraceSource : IEventPipeExecutionTraceSource
    {
        private readonly TraceLogEventSource _source;
        private readonly List<EventPipeExecutionFrame> _leafToRootFrames = new(capacity: 32);
        private SampleProfilerTraceEventParser? _sampleProfiler;
        private Action<EventPipeExecutionSample>? _sampleObserved;

        public TraceEventExecutionTraceSource(TraceLogEventSource source)
        {
            _source = source;
        }

        public long EventsLost => _source.EventsLost;

        public void SubscribeSampleProfilerThreadSample(Action<EventPipeExecutionSample> sampleObserved)
        {
            ArgumentNullException.ThrowIfNull(sampleObserved);
            if (_sampleProfiler is not null)
            {
                throw new InvalidOperationException("The Sample Profiler callback is already registered.");
            }

            _sampleObserved = sampleObserved;
            _sampleProfiler = new SampleProfilerTraceEventParser(_source);
            _sampleProfiler.ThreadSample += OnThreadSample;
        }

        public void Process() => _source.Process();

        public void StopProcessing() => _source.StopProcessing();

        public void Dispose()
        {
            if (_sampleProfiler is not null)
            {
                _sampleProfiler.ThreadSample -= OnThreadSample;
            }

            _sampleObserved = null;
            _sampleProfiler = null;
            _source.Dispose();
        }

        private void OnThreadSample(ClrThreadSampleTraceData data)
        {
            _leafToRootFrames.Clear();
            for (TraceCallStack? stack = data.CallStack(); stack is not null; stack = stack.Caller)
            {
                var address = stack.CodeAddress;
                if (address.Method is null || string.IsNullOrWhiteSpace(address.FullMethodName))
                {
                    continue;
                }

                _leafToRootFrames.Add(new EventPipeExecutionFrame(
                    address.FullMethodName,
                    address.ModuleName,
                    address.ModuleFilePath,
                    ((int)address.CodeAddressIndex).ToString(CultureInfo.InvariantCulture),
                    address));
            }

            _sampleObserved?.Invoke(new EventPipeExecutionSample(
                new DateTimeOffset(data.TimeStamp.ToUniversalTime()),
                data.ThreadID,
                _leafToRootFrames));
        }
    }
}

/// <summary>
/// 从单个 EventPipe Sample Profiler 会话顺序读取并持久化托管执行栈。
/// </summary>
internal sealed class EventPipeExecutionSampler : IEventPipeExecutionSampler
{
    internal const bool DefaultRequestRundown = true;
    internal const int DefaultCircularBufferMegabytes = 32;
    private static readonly TimeSpan DefaultProcessorDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly object _syncRoot = new();
    private readonly ExecutionCaptureStore _store;
    private readonly IEventPipeExecutionRuntime _runtime;
    private readonly TimeSpan _processorDrainTimeout;

    private IEventPipeExecutionSession? _session;
    private IEventPipeExecutionTraceSource? _source;
    private Task? _processing;
    private Task? _stopTask;
    private Task? _disposeTask;
    private DiagnosticsException? _terminalFailure;
    private long _successfulSampleCount;
    private long _completedLostEventCount;
    private int _stopRequested;
    private int _disposed;

    /// <summary>
    /// 创建将已规范化样本写入指定会话存储的采样器。
    /// </summary>
    public EventPipeExecutionSampler(ExecutionCaptureStore store)
        : this(store, new DiagnosticsClientExecutionSamplingRuntime(), DefaultProcessorDrainTimeout)
    {
    }

    internal EventPipeExecutionSampler(
        ExecutionCaptureStore store,
        IEventPipeExecutionRuntime runtime,
        TimeSpan processorDrainTimeout)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        if (processorDrainTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processorDrainTimeout),
                processorDrainTimeout,
                "The processor drain timeout must be positive.");
        }

        _processorDrainTimeout = processorDrainTimeout;
    }

    /// <summary>
    /// 已成功提交到会话存储的样本数。
    /// </summary>
    public long SuccessfulSampleCount => Interlocked.Read(ref _successfulSampleCount);

    /// <summary>
    /// EventPipe 当前已报告的累计丢失事件数。
    /// </summary>
    public long LostEventCount
    {
        get
        {
            var completed = Interlocked.Read(ref _completedLostEventCount);
            lock (_syncRoot)
            {
                return Math.Max(completed, _source?.EventsLost ?? 0);
            }
        }
    }

    /// <summary>
    /// 处理器观察到的首个稳定功能失败。
    /// </summary>
    public DiagnosticsException? TerminalFailure => Volatile.Read(ref _terminalFailure);

    /// <summary>
    /// 启动强类型 Sample Profiler 订阅及后台事件处理器。
    /// </summary>
    public Task StartAsync(TargetProcess target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_processing is not null)
            {
                return Task.CompletedTask;
            }

            if (Volatile.Read(ref _stopRequested) != 0)
            {
                throw new InvalidOperationException("Execution sampling cannot restart after it has stopped.");
            }

            try
            {
                var providers = CreateDefaultProviders();
                _session = _runtime.StartSession(
                    target.ProcessId,
                    providers,
                    DefaultRequestRundown,
                    DefaultCircularBufferMegabytes);
                _source = _runtime.CreateTraceSource(_session);
                _source.SubscribeSampleProfilerThreadSample(OnThreadSample);
                var source = _source;
                _processing = Task.Run(() => ProcessEvents(source), CancellationToken.None);
            }
            catch
            {
                try
                {
                    CleanupSessionLocked();
                }
                catch (Exception)
                {
                }

                throw;
            }
        }

        return Task.CompletedTask;
    }

    internal static EventPipeProvider[] CreateDefaultProviders() =>
    [
        new EventPipeProvider(
            SampleProfilerTraceEventParser.ProviderName,
            EventLevel.Informational),
        new EventPipeProvider(
            ClrTraceEventParser.ProviderName,
            EventLevel.Informational,
            (long)ClrTraceEventParser.Keywords.JITSymbols)
    ];

    /// <summary>
    /// 停止 EventPipe 输入并最多等待五秒排空已启动的处理器。
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task stopTask;
        lock (_syncRoot)
        {
            Interlocked.Exchange(ref _stopRequested, 1);
            _stopTask ??= Task.Run(StopCoreAsync, CancellationToken.None);
            stopTask = _stopTask;
        }

        return cancellationToken.CanBeCanceled
            ? stopTask.WaitAsync(cancellationToken)
            : stopTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_syncRoot)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            Interlocked.Exchange(ref _disposed, 1);
            _disposeTask = DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        ExceptionDispatchInfo? failure = null;
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
        }

        Task? processing;
        IEventPipeExecutionTraceSource? source;
        lock (_syncRoot)
        {
            processing = _processing;
            source = _source;
        }

        try
        {
            try
            {
                source?.StopProcessing();
            }
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
            }

            if (processing is not null)
            {
                try
                {
                    await processing.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure ??= ExceptionDispatchInfo.Capture(exception);
                }
            }
        }
        finally
        {
            lock (_syncRoot)
            {
                try
                {
                    CleanupSessionLocked();
                }
                catch (Exception exception)
                {
                    failure ??= ExceptionDispatchInfo.Capture(exception);
                }
            }
        }

        failure?.Throw();
    }

    private async Task StopCoreAsync()
    {
        Task? processing;
        IEventPipeExecutionSession? session;
        IEventPipeExecutionTraceSource? source;
        lock (_syncRoot)
        {
            processing = _processing;
            session = _session;
            source = _source;
        }

        try
        {
            session?.Stop();
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or UnauthorizedAccessException
                or DiagnosticsClientException)
        {
            SetTerminalFailure(CreateUnavailableException("Execution sampling input could not be stopped.", exception));
            source?.StopProcessing();
        }

        if (processing is null)
        {
            return;
        }

        try
        {
            await processing.WaitAsync(_processorDrainTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            SetTerminalFailure(CreateUnavailableException("Execution sampling processor did not drain in time.", exception));
            source?.StopProcessing();
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or EndOfStreamException)
        {
            SetTerminalFailure(CreateUnavailableException("Execution sampling processor could not stop cleanly.", exception));
        }
    }

    private void OnThreadSample(EventPipeExecutionSample data)
    {
        if (TerminalFailure is not null || data.ThreadId < 0)
        {
            return;
        }

        try
        {
            StoreSample(data);
        }
        catch (DiagnosticsException exception)
            when (exception.ErrorCode is DiagnosticsErrorCode.ExecutionProfileStorageFailed)
        {
            SetTerminalFailure(exception);
            RequestStopAfterFailure();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException)
        {
            SetTerminalFailure(new DiagnosticsException(
                DiagnosticsErrorCode.ExecutionProfileStorageFailed,
                "Execution sample could not be committed.",
                exception));
            RequestStopAfterFailure();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or ArgumentException
                or OverflowException)
        {
            SetTerminalFailure(CreateUnavailableException("Execution sample could not be processed.", exception));
            RequestStopAfterFailure();
        }
    }

    private void StoreSample(EventPipeExecutionSample data)
    {
        var parentStackId = -1;
        for (var index = data.LeafToRootFrames.Count - 1; index >= 0; index--)
        {
            var frame = data.LeafToRootFrames[index];
            if (string.IsNullOrWhiteSpace(frame.MethodName))
            {
                continue;
            }

            var descriptor = new ExecutionFrameDescriptor(
                frame.MethodName,
                NullIfWhiteSpace(frame.ModuleName),
                NullIfWhiteSpace(frame.ModulePath),
                frame.SymbolKey);
            var frameId = GetValueTaskResult(_store.GetOrAddFrameAsync(
                descriptor,
                frame.SymbolAddress,
                CancellationToken.None));
            parentStackId = GetValueTaskResult(_store.GetOrAddStackAsync(
                parentStackId,
                frameId,
                CancellationToken.None));
        }

        if (parentStackId < 0)
        {
            return;
        }

        CompleteValueTask(_store.AppendAsync(
            new ExecutionSampleRecord(data.ObservedAtUtc.ToUniversalTime(), data.ThreadId, parentStackId),
            CancellationToken.None));
        Interlocked.Increment(ref _successfulSampleCount);
    }

    private static T GetValueTaskResult<T>(ValueTask<T> operation)
    {
        if (operation.IsCompletedSuccessfully)
        {
            return operation.Result;
        }

        return operation.AsTask().GetAwaiter().GetResult();
    }

    private static void CompleteValueTask(ValueTask operation)
    {
        if (operation.IsCompletedSuccessfully)
        {
            operation.GetAwaiter().GetResult();
            return;
        }

        operation.AsTask().GetAwaiter().GetResult();
    }

    private void ProcessEvents(IEventPipeExecutionTraceSource source)
    {
        try
        {
            source.Process();
            if (Volatile.Read(ref _stopRequested) == 0 && TerminalFailure is null)
            {
                SetTerminalFailure(CreateUnavailableException(
                    "Execution sampling stream ended unexpectedly.",
                    new EndOfStreamException("The EventPipe execution sampling stream ended.")));
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or IOException
                or EndOfStreamException)
        {
            if (Volatile.Read(ref _stopRequested) == 0)
            {
                SetTerminalFailure(CreateUnavailableException("Execution sampling stream was interrupted.", exception));
            }
        }
        finally
        {
            var eventsLost = source.EventsLost;
            if (eventsLost > 0)
            {
                Interlocked.Add(ref _completedLostEventCount, eventsLost);
            }
        }
    }

    private void RequestStopAfterFailure()
    {
        lock (_syncRoot)
        {
            Interlocked.Exchange(ref _stopRequested, 1);
            _source?.StopProcessing();
            _stopTask ??= Task.Run(
                () => StopCoreAsync(),
                CancellationToken.None);
        }
    }

    private void SetTerminalFailure(DiagnosticsException failure) =>
        Interlocked.CompareExchange(ref _terminalFailure, failure, comparand: null);

    private void CleanupSessionLocked()
    {
        var source = _source;
        var session = _session;
        _source = null;
        _session = null;
        _processing = null;
        try
        {
            source?.Dispose();
        }
        finally
        {
            session?.Dispose();
        }
    }

    private static DiagnosticsException CreateUnavailableException(string message, Exception exception) =>
        new(DiagnosticsErrorCode.ExecutionProfilingUnavailable, message, exception);

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
