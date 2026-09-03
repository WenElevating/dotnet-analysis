using DotnetAnalysis.Core.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using System.Diagnostics.Tracing;

namespace DotnetAnalysis.Diagnostics.Windows;

public sealed class AllocationSampleCollector : IAsyncDisposable
{
    private readonly AllocationProfileBuilder _builder;
    private readonly object _gate = new();
    private EventPipeSession? _session;
    private EventPipeEventSource? _source;
    private Task? _processing;
    private int _disposed;

    public AllocationSampleCollector(AllocationProfileBuilder builder)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
    }

    public void Add(TypeIdentity type, IReadOnlyList<CallStackFrame> frames, long bytes) => _builder.Add(type, frames, bytes);

    public void MarkInterrupted(DateTimeOffset observedAtUtc) => _builder.MarkInterrupted(observedAtUtc);

    public Task StartAsync(TargetProcess target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_processing is not null)
            {
                return Task.CompletedTask;
            }

            try
            {
                _session = new DiagnosticsClient(target.ProcessId).StartEventPipeSession(
                    new EventPipeProvider(
                        "Microsoft-Windows-DotNETRuntime",
                        EventLevel.Verbose,
                        (long)ClrTraceEventParser.Keywords.GCSampledObjectAllocationLow),
                    requestRundown: true,
                    circularBufferMB: 32);
                _source = new EventPipeEventSource(_session.EventStream);
                _source.Clr.AllocationSampling += OnAllocationSampled;
                _processing = Task.Run(() => ProcessEvents(_source), CancellationToken.None);
            }
            catch (Exception exception) when (
                exception is DiagnosticsClientException
                    or IOException
                    or InvalidOperationException
                    or UnauthorizedAccessException)
            {
                CleanupSession();
                throw new global::DotnetAnalysis.Application.Contracts.Diagnostics.DiagnosticsException(
                    global::DotnetAnalysis.Application.Contracts.Diagnostics.DiagnosticsErrorCode.RuntimeNotSupported,
                    "The target runtime does not expose allocation sampling.",
                    exception);
            }
        }

        return Task.CompletedTask;
    }

    public AllocationProfile Seal(DateTimeOffset capturedAtUtc) => _builder.Seal(capturedAtUtc);

    public void BeginNextInterval(DateTimeOffset startedAtUtc) => _builder.BeginNextInterval(startedAtUtc);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Task? processing;
        lock (_gate)
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
                    or UnauthorizedAccessException)
            {
                _builder.MarkInterrupted(DateTimeOffset.UtcNow);
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
                _builder.MarkInterrupted(DateTimeOffset.UtcNow);
            }
        }

        lock (_gate)
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
            Add(
                new TypeIdentity(data.TypeName, null),
                [new CallStackFrame("AllocationSampling", "Microsoft-Windows-DotNETRuntime", null)],
                data.ObjectSize);
        }
        catch (ArgumentException)
        {
            _builder.MarkInterrupted(DateTimeOffset.UtcNow);
        }
    }

    private static void ProcessEvents(EventPipeEventSource source)
    {
        try
        {
            source.Process();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or IOException
                or EndOfStreamException)
        {
            // A target that exits while EventPipe is draining is handled as an
            // interrupted allocation interval by the owner during disposal.
        }
    }

    private void CleanupSession()
    {
        _source?.Dispose();
        _session?.Dispose();
        _source = null;
        _session = null;
        _processing = null;
    }
}
