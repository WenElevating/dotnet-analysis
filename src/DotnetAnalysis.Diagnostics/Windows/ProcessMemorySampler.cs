using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

public interface IManagedHeapReader
{
    long? ReadManagedHeapBytes(int processId);
}

public sealed class UnavailableManagedHeapReader : IManagedHeapReader
{
    public long? ReadManagedHeapBytes(int processId) => null;
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
        _managedHeapReader = managedHeapReader ?? new UnavailableManagedHeapReader();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _interval = interval ?? TimeSpan.FromSeconds(1);
    }

    public async IAsyncEnumerable<MemoryUsageSample> GetSamplesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
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
                yield return new MemoryUsageSample(_timeProvider.GetUtcNow(), null, null, MemoryUsageSampleState.Unavailable);
            }

            await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
        }
    }
}
