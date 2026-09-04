using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 拥有附着期分配采样的生命周期、有限重试、质量状态和区间封存。
/// </summary>
internal sealed class AllocationSamplingSession : IAsyncDisposable
{
    private readonly AllocationProfileBuilder _builder;
    private readonly AllocationCallStackCache _callStackCache;
    private readonly EventPipeAllocationSampler _sampler;
    private int _disposed;

    /// <summary>
    /// 创建分配采样会话。
    /// </summary>
    /// <param name="builder">仅负责按类型和真实调用栈聚合的区间构建器。</param>
    /// <param name="callStackCacheCapacity">当前区间最多常驻的去重调用栈数。</param>
    internal AllocationSamplingSession(
        AllocationProfileBuilder builder,
        int callStackCacheCapacity = 4_096,
        EventPipeAllocationSampler? sampler = null)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _callStackCache = new AllocationCallStackCache(callStackCacheCapacity);
        _sampler = sampler ?? new EventPipeAllocationSampler();
    }

    /// <summary>
    /// 启动目标进程的分配采样。仅对瞬时 EventPipe 启动错误在一秒后重试一次。
    /// </summary>
    public async Task StartAsync(TargetProcess target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await _sampler.StartAsync(target, Record, MarkInterrupted, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (IsTransientStartFailure(exception) && attempt == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is DiagnosticsClientException
                    or IOException
                    or InvalidOperationException
                    or UnauthorizedAccessException)
            {
                throw new DiagnosticsException(
                    DiagnosticsErrorCode.RuntimeNotSupported,
                    "The target runtime does not expose allocation sampling.",
                    exception);
            }
        }
    }

    /// <summary>
    /// 向当前区间追加已解析真实调用栈的分配记录；供受控测试和采样读取器共用。
    /// </summary>
    public void Add(TypeIdentity type, IReadOnlyList<CallStackFrame> frames, long bytes)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
        {
            _builder.MarkCallStackUnavailable();
            return;
        }

        if (_callStackCache.TryGetOrAdd(frames, out var cachedFrames))
        {
            _builder.Add(type, cachedFrames, bytes);
        }
        else
        {
            _builder.MarkCallStackUnavailable();
        }
    }

    /// <summary>
    /// 标记当前区间包含中断或不完整的采样流。
    /// </summary>
    public void MarkInterrupted(DateTimeOffset observedAtUtc) => _builder.MarkInterrupted(observedAtUtc);

    /// <summary>
    /// 冻结当前采样区间的分配概要。
    /// </summary>
    public AllocationProfile Seal(DateTimeOffset capturedAtUtc) => _builder.Seal(capturedAtUtc);

    /// <summary>
    /// 清空已聚合样本并开始新采样区间。
    /// </summary>
    public void BeginNextInterval(DateTimeOffset startedAtUtc) => _builder.BeginNextInterval(startedAtUtc);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _sampler.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or UnauthorizedAccessException)
        {
            MarkInterrupted(DateTimeOffset.UtcNow);
        }
    }

    private void Record(AllocationSamplingRecord record) =>
        Add(record.Type, record.Frames, record.ObservedAllocatedBytes);

    private void MarkInterrupted() => MarkInterrupted(DateTimeOffset.UtcNow);

    private static bool IsTransientStartFailure(Exception exception) =>
        exception is DiagnosticsClientException or IOException;
}
