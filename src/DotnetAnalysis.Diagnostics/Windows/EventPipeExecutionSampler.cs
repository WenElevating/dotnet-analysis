using System.Diagnostics.Tracing;
using System.Globalization;
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

/// <summary>
/// 从单个 EventPipe Sample Profiler 会话顺序读取并持久化托管执行栈。
/// </summary>
internal sealed class EventPipeExecutionSampler : IEventPipeExecutionSampler
{
    private static readonly TimeSpan ProcessorDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly object _syncRoot = new();
    private readonly ExecutionCaptureStore _store;

    private EventPipeSession? _session;
    private TraceLogEventSource? _source;
    private SampleProfilerTraceEventParser? _sampleProfiler;
    private Task? _processing;
    private Task? _stopTask;
    private DiagnosticsException? _terminalFailure;
    private long _successfulSampleCount;
    private long _completedLostEventCount;
    private int _stopRequested;
    private int _disposed;

    /// <summary>
    /// 创建将已规范化样本写入指定会话存储的采样器。
    /// </summary>
    public EventPipeExecutionSampler(ExecutionCaptureStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
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
                var client = new DiagnosticsClient(target.ProcessId);
                var providers = new[]
                {
                    new EventPipeProvider(
                        SampleProfilerTraceEventParser.ProviderName,
                        EventLevel.Informational),
                    new EventPipeProvider(
                        ClrTraceEventParser.ProviderName,
                        EventLevel.Informational,
                        (long)ClrTraceEventParser.Keywords.Default)
                };
                _session = client.StartEventPipeSession(
                    providers,
                    requestRundown: true,
                    circularBufferMB: 32);
                _source = TraceLog.CreateFromEventPipeSession(
                    _session,
                    TraceLog.EventPipeRundownConfiguration.Enable(client));
                _sampleProfiler = new SampleProfilerTraceEventParser(_source);
                _sampleProfiler.ThreadSample += OnThreadSample;
                var source = _source;
                _processing = Task.Run(() => ProcessEvents(source), CancellationToken.None);
            }
            catch
            {
                CleanupSessionLocked();
                throw;
            }
        }

        return Task.CompletedTask;
    }

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
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        lock (_syncRoot)
        {
            CleanupSessionLocked();
        }
    }

    private async Task StopCoreAsync()
    {
        Task? processing;
        EventPipeSession? session;
        TraceLogEventSource? source;
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
            await processing.WaitAsync(ProcessorDrainTimeout, CancellationToken.None).ConfigureAwait(false);
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

    private void OnThreadSample(ClrThreadSampleTraceData data)
    {
        if (TerminalFailure is not null || data.ThreadID < 0)
        {
            return;
        }

        try
        {
            StoreSampleAsync(data).AsTask().GetAwaiter().GetResult();
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

    private async ValueTask StoreSampleAsync(ClrThreadSampleTraceData data)
    {
        var leafToRoot = new List<TraceCodeAddress>();
        for (TraceCallStack? stack = data.CallStack(); stack is not null; stack = stack.Caller)
        {
            var address = stack.CodeAddress;
            if (address.Method is not null && !string.IsNullOrWhiteSpace(address.FullMethodName))
            {
                leafToRoot.Add(address);
            }
        }

        if (leafToRoot.Count == 0)
        {
            return;
        }

        var parentStackId = -1;
        for (var index = leafToRoot.Count - 1; index >= 0; index--)
        {
            var address = leafToRoot[index];
            var descriptor = new ExecutionFrameDescriptor(
                address.FullMethodName,
                NullIfWhiteSpace(address.ModuleName),
                NullIfWhiteSpace(address.ModuleFilePath),
                ((int)address.CodeAddressIndex).ToString(CultureInfo.InvariantCulture));
            var frameId = await _store.GetOrAddFrameAsync(
                descriptor,
                address,
                CancellationToken.None).ConfigureAwait(false);
            parentStackId = await _store.GetOrAddStackAsync(
                parentStackId,
                frameId,
                CancellationToken.None).ConfigureAwait(false);
        }

        var observedAtUtc = new DateTimeOffset(data.TimeStamp.ToUniversalTime());
        await _store.AppendAsync(
            new ExecutionSampleRecord(observedAtUtc, data.ThreadID, parentStackId),
            CancellationToken.None).ConfigureAwait(false);
        Interlocked.Increment(ref _successfulSampleCount);
    }

    private void ProcessEvents(TraceLogEventSource source)
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
        if (_sampleProfiler is not null)
        {
            _sampleProfiler.ThreadSample -= OnThreadSample;
        }

        _source?.Dispose();
        _session?.Dispose();
        _sampleProfiler = null;
        _source = null;
        _session = null;
        _processing = null;
    }

    private static DiagnosticsException CreateUnavailableException(string message, Exception exception) =>
        new(DiagnosticsErrorCode.ExecutionProfilingUnavailable, message, exception);

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
