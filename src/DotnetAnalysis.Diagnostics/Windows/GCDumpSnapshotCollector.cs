using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using System.Diagnostics.Tracing;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// Captures the runtime GC heap EventPipe stream without spawning an external
/// command-line collector. The resulting nettrace-compatible stream is stored under
/// the .gcdump contract and parsed by <see cref="GCDumpSnapshotReader"/>.
/// </summary>
public sealed class GCDumpSnapshotCollector
{
    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromSeconds(30);

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

    private sealed class TeeReadStream : Stream
    {
        private readonly Stream _source;
        private readonly Stream _copy;

        public TeeReadStream(Stream source, Stream copy)
        {
            _source = source;
            _copy = copy;
        }

        public override bool CanRead => _source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _source.Length;
        public override long Position { get => _source.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _copy.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _copy.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _source.Read(buffer, offset, count);
            if (read > 0)
            {
                _copy.Write(buffer, offset, read);
            }

            return read;
        }

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

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
