using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using System.Diagnostics.Tracing;

using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Diagnostics.Windows.Capture;

/// <summary>
/// 直接捕获运行时 GC 堆的 EventPipe 流，不依赖外部命令行收集器；
/// 结果先按 .gcdump 约定保存，再由 <see cref="GCDumpSnapshotReader"/> 解析。
/// </summary>
internal static class GCDumpSnapshotCollector
{
    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 通过目标进程 EventPipe 捕获 GC 堆快照，并转换为可分析的 .gcdump 文件。
    /// </summary>
    /// <param name="target">要捕获的目标进程。</param>
    /// <param name="layout">临时文件和最终文件的存储布局。</param>
    /// <param name="cancellationToken">取消捕获的令牌。</param>
    /// <returns>临时快照路径及捕获完成时间；调用方负责后续提升或清理。</returns>
    public static async Task<(string TemporaryPath, DateTimeOffset CapturedAtUtc)> CaptureAsync(
        TargetProcess target,
        SnapshotStorageLayout layout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(layout);
        cancellationToken.ThrowIfCancellationRequested();

        var temporaryPath = layout.TemporaryPath(Guid.NewGuid());
        Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);

        try
        {
            using var session = new DiagnosticsClient(target.ProcessId).StartEventPipeSession(
                new EventPipeProvider(
                    "Microsoft-Windows-DotNETRuntime",
                    EventLevel.Verbose,
                    (long)ClrTraceEventParser.Keywords.GCHeapSnapshot),
                requestRundown: true,
                circularBufferMB: 128);
            using var source = new EventPipeEventSource(session.EventStream);
            var heapBuilder = new EventPipeHeapBuilder();
            heapBuilder.Attach(source);
            var dumpCompleted = new TaskCompletionSource<DateTimeOffset>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var heapPayloadComplete = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            source.Clr.GCStop += data =>
            {
                if (data.ProcessID == target.ProcessId && heapBuilder.HasHeapData)
                {
                    dumpCompleted.TrySetResult(DateTimeOffset.UtcNow);
                    if (heapBuilder.HasReceivedAllDeclaredEdges)
                    {
                        heapPayloadComplete.TrySetResult();
                    }
                }
            };
            source.Clr.GCBulkEdge += _ =>
            {
                if (heapBuilder.HasReceivedAllDeclaredEdges)
                {
                    heapPayloadComplete.TrySetResult();
                }
            };

            var processing = Task.Run(() => ProcessSource(source), CancellationToken.None);
            var completed = await Task.WhenAny(
                heapPayloadComplete.Task,
                processing,
                Task.Delay(s_defaultTimeout, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            StopSession(session);

            await processing.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (completed != heapPayloadComplete.Task
                || !heapPayloadComplete.Task.IsCompletedSuccessfully
                || !dumpCompleted.Task.IsCompletedSuccessfully
                || source.EventsLost != 0)
            {
                var diagnostics = heapBuilder.GetDiagnostics();
                throw new TimeoutException(
                    $"The EventPipe heap snapshot did not deliver all declared edges and the matching GC completion event before the timeout or while draining the stopped session. " +
                    $"GCStart={diagnostics.GcStartCount}, GCStop={diagnostics.GcStopCount}, " +
                    $"NodeBatches={diagnostics.NodeBatchCount}, Nodes={diagnostics.NodeCount}, " +
                    $"EdgeBatches={diagnostics.EdgeBatchCount}, Edges={diagnostics.EdgeCount}, " +
                    $"RootEdgeBatches={diagnostics.RootEdgeBatchCount}, RootEdges={diagnostics.RootEdgeCount}, " +
                    $"FirstNodeUtc={diagnostics.FirstNodeUtc:O}, LastNodeUtc={diagnostics.LastNodeUtc:O}, " +
                    $"GcStartObservedMilliseconds={diagnostics.GcStartObservedMilliseconds}, " +
                    $"GcStopObservedMilliseconds={diagnostics.GcStopObservedMilliseconds}, " +
                    $"FirstNodeObservedMilliseconds={diagnostics.FirstNodeObservedMilliseconds}, " +
                    $"LastNodeObservedMilliseconds={diagnostics.LastNodeObservedMilliseconds}, " +
                    $"LastEdgeObservedMilliseconds={diagnostics.LastEdgeObservedMilliseconds}, " +
                    $"LastRootEdgeObservedMilliseconds={diagnostics.LastRootEdgeObservedMilliseconds}, " +
                    $"EventPipeEventsLost={source.EventsLost}.");
            }
            var capturedAtUtc = await dumpCompleted.Task.ConfigureAwait(false);
            GCDumpFastSerializationWriter.Write(
                temporaryPath,
                heapBuilder,
                target,
                capturedAtUtc,
                cancellationToken);
            return (temporaryPath, capturedAtUtc);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            await MemorySnapshotStore.DeleteTemporaryAsync(temporaryPath).ConfigureAwait(false);
            throw new DiagnosticsException(
                DiagnosticsErrorCode.CaptureCancelled,
                "Snapshot capture was cancelled.",
                exception);
        }
        catch (DiagnosticsException)
        {
            await MemorySnapshotStore.DeleteTemporaryAsync(temporaryPath).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (
            exception is DiagnosticsClientException
                or IOException
                or InvalidOperationException
                or UnauthorizedAccessException
                or TimeoutException)
        {
            await MemorySnapshotStore.DeleteTemporaryAsync(temporaryPath).ConfigureAwait(false);
            throw new DiagnosticsException(
                DiagnosticsErrorCode.CaptureFailed,
                "The runtime GC heap snapshot could not be captured.",
                exception);
        }
    }

    /// <summary>
    /// 在后台驱动 TraceEvent 消费 EventPipe 数据流。
    /// </summary>
    private static void ProcessSource(EventPipeEventSource source)
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
        }
    }

    /// <summary>
    /// 请求运行时停止 EventPipe 会话，同时允许已发送的末尾堆事件继续被读取。
    /// </summary>
    private static void StopSession(EventPipeSession session)
    {
        try
        {
            session.Stop();
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or DiagnosticsClientException)
        {
        }
    }

}
