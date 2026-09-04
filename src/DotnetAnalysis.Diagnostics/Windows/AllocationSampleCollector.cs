using DotnetAnalysis.Core.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using System.Diagnostics.Tracing;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 通过 EventPipe 接收低开销分配采样并维护当前采样区间。
/// </summary>
public sealed class AllocationSampleCollector : IAsyncDisposable
{
    private readonly AllocationProfileBuilder _builder;
    private readonly object _gate = new();
    private EventPipeSession? _session;
    private EventPipeEventSource? _source;
    private Task? _processing;
    private int _disposed;

    /// <summary>
    /// 创建用于接收 EventPipe 分配采样的收集器。
    /// </summary>
    /// <param name="builder">聚合采样结果的构建器。</param>
    public AllocationSampleCollector(AllocationProfileBuilder builder)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
    }

    /// <summary>
    /// 向当前区间手工追加一条分配采样记录。
    /// </summary>
    public void Add(TypeIdentity type, IReadOnlyList<CallStackFrame> frames, long bytes) => _builder.Add(type, frames, bytes);

    /// <summary>
    /// 标记当前区间包含中断或不完整的采样流。
    /// </summary>
    public void MarkInterrupted(DateTimeOffset observedAtUtc) => _builder.MarkInterrupted(observedAtUtc);

    /// <summary>
    /// 启动目标进程的 EventPipe 分配采样；重复启动会复用已有会话。
    /// </summary>
    /// <param name="target">要采样的目标进程身份。</param>
    /// <param name="cancellationToken">启动前检查的取消令牌。</param>
    /// <returns>会话已启动或已处于运行状态时完成的任务。</returns>
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

    /// <summary>
    /// 冻结当前采样区间的分配概要。
    /// </summary>
    public AllocationProfile Seal(DateTimeOffset capturedAtUtc) => _builder.Seal(capturedAtUtc);

    /// <summary>
    /// 清空已聚合的样本并开始新的采样区间。
    /// </summary>
    public void BeginNextInterval(DateTimeOffset startedAtUtc) => _builder.BeginNextInterval(startedAtUtc);

    /// <summary>
    /// 停止 EventPipe 会话并在有限等待后释放资源；停止异常会降低数据完整性标记。
    /// </summary>
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

    /// <summary>
    /// 把单条 EventPipe 分配事件转换为核心采样记录。
    /// </summary>
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

    /// <summary>
    /// 驱动 TraceEvent 读取循环，并吞掉目标退出时的流结束异常。
    /// </summary>
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

    /// <summary>
    /// 释放 EventPipe 读取器和会话并清空运行状态。
    /// </summary>
    private void CleanupSession()
    {
        _source?.Dispose();
        _session?.Dispose();
        _source = null;
        _session = null;
        _processing = null;
    }
}
