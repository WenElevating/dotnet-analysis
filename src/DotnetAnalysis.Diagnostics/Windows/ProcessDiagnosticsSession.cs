using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

public sealed class ProcessDiagnosticsSession : IProcessDiagnosticsSession
{
    private readonly ProcessMemorySampler _sampler;
    private readonly Func<CancellationToken, Task<MemorySnapshot>> _capture;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ProcessDiagnosticsSessionId _id = ProcessDiagnosticsSessionId.New();
    private int _disposed;

    public ProcessDiagnosticsSession(
        TargetProcess process,
        ProcessMemorySampler sampler,
        Func<CancellationToken, Task<MemorySnapshot>>? capture = null)
    {
        Process = process ?? throw new ArgumentNullException(nameof(process));
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _capture = capture ?? (_ => Task.FromException<MemorySnapshot>(new DiagnosticsException(
            DiagnosticsErrorCode.RuntimeNotSupported,
            "Live snapshot capture is not available on this adapter.")));
    }

    public TargetProcess Process { get; }

    public ProcessDiagnosticsSessionId Id => _id;

    public ProcessDiagnosticsSessionState State => Volatile.Read(ref _disposed) == 0
        ? ProcessDiagnosticsSessionState.Monitoring
        : ProcessDiagnosticsSessionState.Ended;

    public Task EndAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DisposeAsync().AsTask();
    }

    public async IAsyncEnumerable<MemoryUsageSample> GetMemoryUsageAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await foreach (var sample in _sampler.GetSamplesAsync(linked.Token).ConfigureAwait(false))
        {
            yield return sample;
        }
    }

    public Task<MemorySnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _capture(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
