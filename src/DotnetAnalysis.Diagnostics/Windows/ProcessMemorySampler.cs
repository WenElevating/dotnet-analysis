using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using System.Collections;
using System.Diagnostics.Tracing;
using System.Globalization;

namespace DotnetAnalysis.Diagnostics.Windows;

public interface IManagedHeapReader
{
    long? ReadManagedHeapBytes(int processId);
}

public sealed class UnavailableManagedHeapReader : IManagedHeapReader
{
    public long? ReadManagedHeapBytes(int processId) => null;
}

public sealed class EventPipeManagedHeapReader : IManagedHeapReader
{
    private const string RuntimeProviderName = "System.Runtime";
    private const string EventCountersName = "EventCounters";
    private const string ManagedHeapCounterName = "gc-heap-size";
    private readonly TimeSpan _timeout;

    public EventPipeManagedHeapReader(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    public long? ReadManagedHeapBytes(int processId)
    {
        if (processId <= 0 || !OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var session = new DiagnosticsClient(processId).StartEventPipeSession(
                new EventPipeProvider(
                    RuntimeProviderName,
                    EventLevel.Informational,
                    0,
                    new Dictionary<string, string>
                    {
                        ["EventCounterIntervalSec"] = "0.2"
                    }),
                requestRundown: false,
                circularBufferMB: 16);
            using var source = new EventPipeEventSource(session.EventStream);
            var result = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            // EventCounters is emitted as a dynamic event.  Registering through
            // Dynamic.All is intentionally used here instead of relying on the
            // provider/event lookup table: the EventPipe payload is generated at
            // runtime and the lookup table is not populated consistently across
            // .NET 8, .NET 9 and .NET 10.
            source.Dynamic.All += traceEvent =>
            {
                if (string.Equals(traceEvent.EventName, EventCountersName, StringComparison.Ordinal))
                {
                    TryReadCounter(traceEvent, result);
                }
            };

            var processing = Task.Run(() => source.Process());
            try
            {
                return result.Task.WaitAsync(_timeout).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                return null;
            }
            finally
            {
                source.StopProcessing();
                session.Stop();
                try
                {
                    processing.Wait(TimeSpan.FromSeconds(1));
                }
                catch (AggregateException)
                {
                }
            }
        }
        catch (Exception exception) when (
            exception is DiagnosticsClientException
                or IOException
                or InvalidOperationException
                or UnauthorizedAccessException
                or TimeoutException)
        {
            return null;
        }
    }

    private static void TryReadCounter(
        TraceEvent traceEvent,
        TaskCompletionSource<long> result)
    {
        try
        {
            // TraceEvent represents nested EventCounters payloads as its
            // generic StructValue (IDictionary<string, object>), not the
            // legacy non-generic IDictionary.  The latter silently caused
            // every counter to be discarded on current .NET runtimes.
            if (traceEvent.PayloadValue(0) is not IDictionary<string, object> payload
                || payload["Payload"] is not IDictionary<string, object> counter)
            {
                return;
            }

            var name = counter["Name"]?.ToString();
            if (!string.Equals(name, ManagedHeapCounterName, StringComparison.Ordinal))
            {
                return;
            }

            var rawValue = counter["Mean"] ?? counter["Increment"];
            if (rawValue is not null
                && double.TryParse(rawValue.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && value >= 0
                && value <= long.MaxValue)
            {
                result.TrySetResult((long)value);
            }
        }
        catch (Exception exception) when (
            exception is KeyNotFoundException
                or FormatException
                or InvalidCastException
                or IndexOutOfRangeException
                or ArgumentException)
        {
        }
    }
}

public sealed class ProcessMemorySampler
{
    private readonly TargetProcess _target;
    private readonly IProcessMemoryReader _processMemoryReader;
    private readonly IManagedHeapReader _managedHeapReader;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;

    public ProcessMemorySampler(
        TargetProcess target,
        IProcessMemoryReader? processMemoryReader = null,
        IManagedHeapReader? managedHeapReader = null,
        TimeProvider? timeProvider = null,
        TimeSpan? interval = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _processMemoryReader = processMemoryReader ?? new ProcessMemoryReader();
        _managedHeapReader = managedHeapReader ?? new EventPipeManagedHeapReader();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _interval = interval ?? TimeSpan.FromSeconds(1);
    }

    public async IAsyncEnumerable<MemoryUsageSample> GetSamplesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_processMemoryReader is ProcessMemoryReader
                && !ProcessMemoryReader.IsProcessAlive(_target.ProcessId))
            {
                yield return new MemoryUsageSample(
                    _timeProvider.GetUtcNow(),
                    null,
                    null,
                    MemoryUsageSampleState.SessionEnded);
                yield break;
            }

            var processMemory = _processMemoryReader.ReadPrivateWorkingSetBytes(_target.ProcessId);
            var managedHeap = _managedHeapReader.ReadManagedHeapBytes(_target.ProcessId);
            if (processMemory is null && managedHeap is null)
            {
                yield return new MemoryUsageSample(_timeProvider.GetUtcNow(), null, null, MemoryUsageSampleState.Unavailable);
            }
            else if (processMemory is not null && managedHeap is not null)
            {
                yield return new MemoryUsageSample(_timeProvider.GetUtcNow(), managedHeap, processMemory, MemoryUsageSampleState.Measured);
            }
            else
            {
                if (_processMemoryReader is ProcessMemoryReader
                    && !ProcessMemoryReader.IsProcessAlive(_target.ProcessId))
                {
                    yield return new MemoryUsageSample(
                        _timeProvider.GetUtcNow(),
                        null,
                        null,
                        MemoryUsageSampleState.SessionEnded);
                    yield break;
                }

                yield return new MemoryUsageSample(_timeProvider.GetUtcNow(), null, null, MemoryUsageSampleState.Unavailable);
            }

            await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
        }
    }
}
