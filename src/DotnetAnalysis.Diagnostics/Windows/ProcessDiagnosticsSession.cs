using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 把 Windows 进程采样、快照捕获和资源生命周期统一为诊断会话。
/// </summary>
public sealed class ProcessDiagnosticsSession : IProcessDiagnosticsSession
{
    private readonly ProcessMemorySampler _sampler;
    private readonly Func<CancellationToken, Task<MemorySnapshot>> _capture;
    private readonly AllocationSampleCollector? _allocationCollector;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ProcessDiagnosticsSessionId _id = ProcessDiagnosticsSessionId.New();
    private int _disposed;

    /// <summary>
    /// 创建一个绑定目标进程、内存采样和可选快照捕获器的诊断会话。
    /// </summary>
    /// <param name="process">会话绑定的目标进程身份。</param>
    /// <param name="sampler">提供进程内存使用样本的采样器。</param>
    /// <param name="capture">异步捕获快照的委托；省略时表示当前适配器不支持实时捕获。</param>
    /// <param name="allocationCollector">可选的分配采样收集器。</param>
    public ProcessDiagnosticsSession(
        TargetProcess process,
        ProcessMemorySampler sampler,
        Func<CancellationToken, Task<MemorySnapshot>>? capture = null,
        AllocationSampleCollector? allocationCollector = null)
    {
        Process = process ?? throw new ArgumentNullException(nameof(process));
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _allocationCollector = allocationCollector;
        _capture = capture ?? (_ => Task.FromException<MemorySnapshot>(new DiagnosticsException(
            DiagnosticsErrorCode.RuntimeNotSupported,
            "Live snapshot capture is not available on this adapter.")));
    }

    /// <summary>
    /// 会话绑定的目标进程。
    /// </summary>
    public TargetProcess Process { get; }

    /// <summary>
    /// 当前诊断会话的稳定标识。
    /// </summary>
    public ProcessDiagnosticsSessionId Id => _id;

    /// <summary>
    /// 会话未释放时为监控中，释放后为已结束。
    /// </summary>
    public ProcessDiagnosticsSessionState State => Volatile.Read(ref _disposed) == 0
        ? ProcessDiagnosticsSessionState.Monitoring
        : ProcessDiagnosticsSessionState.Ended;

    /// <summary>
    /// 结束会话并取消正在进行的内存采样。
    /// </summary>
    /// <param name="cancellationToken">结束前检查的取消令牌。</param>
    public Task EndAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DisposeAsync().AsTask();
    }

    /// <summary>
    /// 持续产生内存使用样本；会话结束时通过取消内部令牌停止。
    /// </summary>
    public async IAsyncEnumerable<MemoryUsageSample> GetMemoryUsageAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await foreach (var sample in _sampler.GetSamplesAsync(linked.Token).ConfigureAwait(false))
        {
            yield return sample;
        }
    }

    /// <summary>
    /// 捕获一次内存快照；会话结束后调用会抛出 <see cref="ObjectDisposedException"/>。
    /// </summary>
    public Task<MemorySnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _capture(cancellationToken);
    }

    /// <summary>
    /// 幂等释放会话生命周期令牌及分配采样资源。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        }

        if (_allocationCollector is not null)
        {
            return DisposeCollectorAsync(_allocationCollector);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 异步释放可选的分配采样收集器。
    /// </summary>
    private static async ValueTask DisposeCollectorAsync(AllocationSampleCollector collector)
    {
        await collector.DisposeAsync().ConfigureAwait(false);
    }
}
