using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using System.Diagnostics.Tracing;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 直接捕获运行时 GC 堆的 EventPipe 流，不依赖外部命令行收集器；
/// 结果先按 .gcdump 约定保存，再由 <see cref="GCDumpSnapshotReader"/> 解析。
/// </summary>
public sealed class GCDumpSnapshotCollector
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
        string? serializedPath = null;
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
            await using var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var source = new EventPipeEventSource(new TeeReadStream(session.EventStream, output));
            var dumpCompleted = new TaskCompletionSource<DateTimeOffset>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var sawHeapData = 0;

            source.Clr.GCStart += data =>
            {
                if (data.ProcessID == target.ProcessId && data.Depth == 2)
                {
                    Volatile.Write(ref sawHeapData, 1);
                }
            };
            source.Clr.GCBulkNode += data =>
            {
                if (data.ProcessID == target.ProcessId && data.Count > 0)
                {
                    Volatile.Write(ref sawHeapData, 1);
                }
            };
            source.Clr.GCStop += data =>
            {
                if (data.ProcessID == target.ProcessId && Volatile.Read(ref sawHeapData) != 0)
                {
                    dumpCompleted.TrySetResult(DateTimeOffset.UtcNow);
                    source.StopProcessing();
                }
            };

            var processing = Task.Run(() => ProcessSource(source), CancellationToken.None);
            var completed = await Task.WhenAny(
                dumpCompleted.Task,
                processing,
                Task.Delay(s_defaultTimeout, cancellationToken)).ConfigureAwait(false);
            if (completed != dumpCompleted.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("The target did not produce a complete GC heap snapshot before the timeout.");
            }

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

            await processing.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            source.Dispose();
            await output.DisposeAsync().ConfigureAwait(false);

            // EventPipe emits a nettrace stream.  Convert the completed heap
            // graph into the official FastSerialization .gcdump envelope
            // before exposing the temporary file to the promotion layer.
            var heap = GCDumpSnapshotReader.ReadHeapForSerialization(temporaryPath, cancellationToken);
            serializedPath = layout.TemporaryPath(Guid.NewGuid());
            GCDumpFastSerializationWriter.Write(
                serializedPath,
                heap,
                target,
                await dumpCompleted.Task.ConfigureAwait(false),
                cancellationToken);
            await MemorySnapshotStore.DeleteTemporaryAsync(temporaryPath).ConfigureAwait(false);
            File.Move(serializedPath, temporaryPath, false);
            serializedPath = null;
            return (temporaryPath, await dumpCompleted.Task.ConfigureAwait(false));
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            await MemorySnapshotStore.DeleteTemporaryAsync(temporaryPath).ConfigureAwait(false);
            if (serializedPath is not null)
            {
                await MemorySnapshotStore.DeleteTemporaryAsync(serializedPath).ConfigureAwait(false);
            }
            throw new DiagnosticsException(
                DiagnosticsErrorCode.CaptureCancelled,
                "Snapshot capture was cancelled.",
                exception);
        }
        catch (DiagnosticsException)
        {
            await MemorySnapshotStore.DeleteTemporaryAsync(temporaryPath).ConfigureAwait(false);
            if (serializedPath is not null)
            {
                await MemorySnapshotStore.DeleteTemporaryAsync(serializedPath).ConfigureAwait(false);
            }
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
            if (serializedPath is not null)
            {
                await MemorySnapshotStore.DeleteTemporaryAsync(serializedPath).ConfigureAwait(false);
            }
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
    /// 读取 EventPipe 时把字节流同步复制到快照文件。
    /// </summary>
    private sealed class TeeReadStream : Stream
    {
        private readonly Stream _source;
        private readonly Stream _copy;

        /// <summary>
        /// 创建一个将读取内容同时转发到副本流的只读流。
        /// </summary>
        public TeeReadStream(Stream source, Stream copy)
        {
            _source = source;
            _copy = copy;
        }

        /// <summary>
        /// 报告源流是否可读。
        /// </summary>
        public override bool CanRead => _source.CanRead;

        /// <summary>
        /// 该转发流不支持定位。
        /// </summary>
        public override bool CanSeek => false;

        /// <summary>
        /// 该转发流不支持写入。
        /// </summary>
        public override bool CanWrite => false;

        /// <summary>
        /// 返回源流长度。
        /// </summary>
        public override long Length => _source.Length;

        /// <summary>
        /// 读取或拒绝设置源流当前位置。
        /// </summary>
        public override long Position { get => _source.Position; set => throw new NotSupportedException(); }

        /// <summary>
        /// 刷新副本流。
        /// </summary>
        public override void Flush() => _copy.Flush();

        /// <summary>
        /// 异步刷新副本流。
        /// </summary>
        public override Task FlushAsync(CancellationToken cancellationToken) => _copy.FlushAsync(cancellationToken);

        /// <summary>
        /// 读取源流并把读取字节写入副本流。
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _source.Read(buffer, offset, count);
            if (read > 0)
            {
                _copy.Write(buffer, offset, read);
            }

            return read;
        }

        /// <summary>
        /// 异步读取源流并把读取字节写入副本流。
        /// </summary>
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                await _copy.WriteAsync(buffer[..read], cancellationToken).ConfigureAwait(false);
            }

            return read;
        }

        /// <summary>
        /// 拒绝对只读转发流执行定位。
        /// </summary>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <summary>
        /// 拒绝修改只读转发流长度。
        /// </summary>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>
        /// 拒绝向只读转发流写入数据。
        /// </summary>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
