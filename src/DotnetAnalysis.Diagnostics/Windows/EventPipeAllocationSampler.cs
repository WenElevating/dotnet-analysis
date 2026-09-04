using System.Diagnostics.Tracing;
using DotnetAnalysis.Core.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 表示 EventPipe 读取到的一条原始分配采样记录。
/// </summary>
internal sealed record AllocationSamplingRecord(
    TypeIdentity Type,
    long ObservedAllocatedBytes,
    IReadOnlyList<CallStackFrame> Frames);

/// <summary>
/// 只负责从 EventPipe 读取分配、调用栈及丢失事件，不持有区间聚合状态。
/// </summary>
internal sealed class EventPipeAllocationSampler : IAsyncDisposable
{
    private readonly object _syncRoot = new();
    private EventPipeSession? _session;
    private TraceLogEventSource? _source;
    private Task? _processing;
    private Action<AllocationSamplingRecord>? _recordObserved;
    private Action? _interrupted;
    private int _disposed;

    /// <summary>
    /// 启动 EventPipe 分配采样并将原始记录交给调用方。
    /// </summary>
    public Task StartAsync(
        TargetProcess target,
        Action<AllocationSamplingRecord> recordObserved,
        Action interrupted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(recordObserved);
        ArgumentNullException.ThrowIfNull(interrupted);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_processing is not null)
            {
                return Task.CompletedTask;
            }

            _recordObserved = recordObserved;
            _interrupted = interrupted;
            try
            {
                var client = new DiagnosticsClient(target.ProcessId);
                _session = client.StartEventPipeSession(
                    new EventPipeProvider(
                        "Microsoft-Windows-DotNETRuntime",
                        EventLevel.Verbose,
                        (long)(ClrTraceEventParser.Keywords.GCSampledObjectAllocationLow | ClrTraceEventParser.Keywords.Stack)),
                    requestRundown: false,
                    circularBufferMB: 32);
                _source = TraceLog.CreateFromEventPipeSession(
                    _session,
                    TraceLog.EventPipeRundownConfiguration.Enable(client));
                _source.Clr.AllocationSampling += OnAllocationSampled;
                _processing = Task.Run(ProcessEvents, CancellationToken.None);
            }
            catch
            {
                CleanupSession();
                throw;
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Task? processing;
        lock (_syncRoot)
        {
            processing = _processing;
            try
            {
                _source?.StopProcessing();
                _session?.Stop();
            }
            catch (Exception exception) when (
                exception is IOException
                    or InvalidOperationException
                    or UnauthorizedAccessException
                    or DiagnosticsClientException)
            {
                _interrupted?.Invoke();
            }
        }

        if (processing is not null)
        {
            try
            {
                await processing.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is TimeoutException
                    or OperationCanceledException
                    or InvalidOperationException
                    or IOException)
            {
                _interrupted?.Invoke();
            }
        }

        lock (_syncRoot)
        {
            CleanupSession();
        }
    }

    private void OnAllocationSampled(AllocationSampledTraceData data)
    {
        if (data.ObjectSize <= 0 || string.IsNullOrWhiteSpace(data.TypeName))
        {
            return;
        }

        try
        {
            _recordObserved?.Invoke(new AllocationSamplingRecord(
                new TypeIdentity(data.TypeName, null),
                data.ObjectSize,
                ResolveFrames(data)));
        }
        catch (ArgumentException)
        {
            _interrupted?.Invoke();
        }
    }

    private static List<CallStackFrame> ResolveFrames(AllocationSampledTraceData data)
    {
        var frames = new List<CallStackFrame>();
        for (TraceCallStack? stack = data.CallStack(); stack is not null; stack = stack.Caller)
        {
            var address = stack.CodeAddress;
            var name = address.FullMethodName;
            if (!string.IsNullOrWhiteSpace(name))
            {
                frames.Add(new CallStackFrame(name, address.ModuleName, null));
            }
        }

        return frames;
    }

    private void ProcessEvents()
    {
        try
        {
            _source!.Process();
            if (_source.EventsLost != 0)
            {
                _interrupted?.Invoke();
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or IOException
                or EndOfStreamException)
        {
            _interrupted?.Invoke();
        }
    }

    private void CleanupSession()
    {
        _source?.Dispose();
        _session?.Dispose();
        _source = null;
        _session = null;
        _processing = null;
        _recordObserved = null;
        _interrupted = null;
    }
}
