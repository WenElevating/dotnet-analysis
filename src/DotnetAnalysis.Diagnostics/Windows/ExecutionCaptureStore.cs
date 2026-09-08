using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DotnetAnalysis.Diagnostics.Windows;

internal readonly record struct ExecutionSampleRecord(
    DateTimeOffset ObservedAtUtc,
    int ThreadId,
    int StackId);

internal readonly record struct ExecutionStackSampleCount(
    int StackId,
    long SampleCount);

internal sealed record ExecutionStackSampleCounts(
    long ReceivedSampleCount,
    IReadOnlyList<ExecutionStackSampleCount> Stacks);

internal readonly record struct ExecutionFrameDescriptor(
    string MethodName,
    string? ModuleName,
    string? ModulePath,
    string SymbolKey);

internal sealed record ExecutionFrameReference(
    int FrameId,
    ExecutionFrameDescriptor Descriptor,
    TraceCodeAddress? SymbolAddress);

internal sealed record ExecutionCaptureReadBoundary(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset WrittenThroughUtc,
    long LastCompletedRecord)
{
    internal IReadOnlyList<ExecutionCaptureSegmentReadLimit> SegmentReadLimits { get; init; } = [];
}

/// <summary>
/// 为单个诊断会话追加、读取并在销毁时清理执行采样数据。
/// </summary>
internal sealed class ExecutionCaptureStore : IAsyncDisposable
{
    private const int SegmentHeaderLength = 48;
    private const int DefaultSegmentDataLimitBytes = 4 * 1024 * 1024;
    private const int MaximumEncodedRecordLength = 30;
    private const int SegmentFormatVersion = 3;
    private static ReadOnlySpan<byte> SegmentMagic => "ECS1"u8;

    private readonly ExecutionCaptureStorageLayout _layout;
    private readonly int _segmentDataLimitBytes;
    private readonly Func<Task>? _beforeBoundaryPublishAsync;
    private readonly Func<Task>? _beforeStackFramesReadAsync;
    private readonly Func<Task>? _afterDisposeWriterAcquiredAsync;
    private readonly Action? _beforeSegmentFlush;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly object _stateLock = new();
    private readonly Dictionary<ExecutionFrameDescriptor, int> _frameIds = [];
    private readonly Dictionary<ExecutionStackKey, int> _stackIds = [];
    private readonly Dictionary<int, int> _threadIndexes = [];
    private readonly List<ExecutionFrameReference> _framesById = [];
    private readonly List<ExecutionStackKey> _stacksById = [];
    private readonly List<int> _threadIdsByIndex = [];
    private readonly List<ExecutionCaptureSegment> _segments = [];
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;

    private ExecutionCaptureSegment? _currentSegment;
    private DateTimeOffset _writtenThroughUtc;
    private long _lastCompletedRecord = -1;
    private int _activeReaderCount;
    private bool _disposeStarted;
    private DiagnosticsException? _writeFailure;
    private TaskCompletionSource? _readersDrained;
    private Task? _disposeTask;

    /// <summary>
    /// 创建一个会话私有的执行采样存储。
    /// </summary>
    /// <param name="layout">会话目录和样本段路径布局。</param>
    /// <param name="segmentDataLimitBytes">单个样本段数据区的最大字节数。</param>
    public ExecutionCaptureStore(
        ExecutionCaptureStorageLayout layout,
        int segmentDataLimitBytes = DefaultSegmentDataLimitBytes,
        Func<Task>? beforeBoundaryPublishAsync = null,
        Func<Task>? beforeStackFramesReadAsync = null,
        Func<Task>? afterDisposeWriterAcquiredAsync = null,
        Action? beforeSegmentFlush = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentDataLimitBytes, 3);

        _segmentDataLimitBytes = segmentDataLimitBytes;
        _beforeBoundaryPublishAsync = beforeBoundaryPublishAsync;
        _beforeStackFramesReadAsync = beforeStackFramesReadAsync;
        _afterDisposeWriterAcquiredAsync = afterDisposeWriterAcquiredAsync;
        _beforeSegmentFlush = beforeSegmentFlush;
        _writtenThroughUtc = _startedAtUtc;
    }

    /// <summary>
    /// 获取或添加帧描述符的会话内标识。
    /// </summary>
    public ValueTask<int> GetOrAddFrameAsync(
        ExecutionFrameDescriptor frame,
        CancellationToken cancellationToken) =>
        GetOrAddFrameAsync(frame, symbolAddress: null, cancellationToken);

    /// <summary>
    /// 获取或添加带运行时符号句柄的帧描述符，并返回会话内稳定标识。
    /// </summary>
    public ValueTask<int> GetOrAddFrameAsync(
        ExecutionFrameDescriptor frame,
        TraceCodeAddress? symbolAddress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frame.MethodName);
        ArgumentException.ThrowIfNullOrWhiteSpace(frame.SymbolKey);
        cancellationToken.ThrowIfCancellationRequested();

        _writer.Wait(cancellationToken);
        try
        {
            lock (_stateLock)
            {
                ThrowIfDisposingLocked();
                if (_frameIds.TryGetValue(frame, out var existingId))
                {
                    if (_framesById[existingId].SymbolAddress is null && symbolAddress is not null)
                    {
                        _framesById[existingId] = _framesById[existingId] with { SymbolAddress = symbolAddress };
                    }

                    return ValueTask.FromResult(existingId);
                }

                var frameId = _frameIds.Count;
                _frameIds.Add(frame, frameId);
                _framesById.Add(new ExecutionFrameReference(frameId, frame, symbolAddress));
                return ValueTask.FromResult(frameId);
            }
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>
    /// 获取或添加调用栈节点的会话内标识。
    /// </summary>
    public ValueTask<int> GetOrAddStackAsync(int parentStackId, int frameId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parentStackId, -1);
        ArgumentOutOfRangeException.ThrowIfNegative(frameId);

        cancellationToken.ThrowIfCancellationRequested();
        _writer.Wait(cancellationToken);
        try
        {
            lock (_stateLock)
            {
                ThrowIfDisposingLocked();
                var key = new ExecutionStackKey(parentStackId, frameId);
                if (_stackIds.TryGetValue(key, out var existingId))
                {
                    return ValueTask.FromResult(existingId);
                }

                var stackId = _stackIds.Count;
                _stackIds.Add(key, stackId);
                _stacksById.Add(key);
                return ValueTask.FromResult(stackId);
            }
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>
    /// 获取指定调用栈从根到叶、包含稳定帧标识的引用防御性副本。
    /// </summary>
    /// <param name="stackId">会话内调用栈标识。</param>
    /// <param name="cancellationToken">取消读取等待和调用链还原的令牌。</param>
    /// <returns>从根帧到叶帧、不可修改的帧引用集合。</returns>
    /// <exception cref="ArgumentOutOfRangeException">调用栈标识无效时引发。</exception>
    /// <exception cref="OperationCanceledException">操作被取消时引发。</exception>
    public async ValueTask<IReadOnlyList<ExecutionFrameReference>> GetStackFramesAsync(
        int stackId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stackId);
        cancellationToken.ThrowIfCancellationRequested();

        if (_beforeStackFramesReadAsync is not null)
        {
            await _beforeStackFramesReadAsync().ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateLock)
        {
            if ((uint)stackId >= (uint)_stacksById.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(stackId), stackId, "Stack identifier is not known by this execution capture store.");
            }

            var frames = new List<ExecutionFrameReference>();
            var currentStackId = stackId;
            while (currentStackId >= 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stack = _stacksById[currentStackId];
                frames.Add(_framesById[stack.FrameId]);
                currentStackId = stack.ParentStackId;
            }

            frames.Reverse();
            return Array.AsReadOnly(frames.ToArray());
        }
    }

    /// <summary>
    /// 追加一个已构建调用栈的执行采样记录。
    /// </summary>
    public ValueTask AppendAsync(ExecutionSampleRecord sample, CancellationToken cancellationToken)
    {
        ValidateSample(sample);
        cancellationToken.ThrowIfCancellationRequested();
        if (_beforeBoundaryPublishAsync is not null)
        {
            return new ValueTask(AppendWithBoundaryHookAsync(sample, cancellationToken));
        }

        AppendSynchronously(sample, cancellationToken);
        return ValueTask.CompletedTask;
    }

    private static void ValidateSample(ExecutionSampleRecord sample)
    {
        if (sample.ThreadId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sample), "Thread identifier must not be negative.");
        }

        if (sample.StackId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sample), "Stack identifier must not be negative.");
        }
    }

    private void AppendSynchronously(ExecutionSampleRecord sample, CancellationToken cancellationToken)
    {
        _writer.Wait(cancellationToken);
        try
        {
            try
            {
                var pendingAppend = WriteSample(sample);
                PublishAppend(pendingAppend);
            }
            catch (Exception exception) when (
                exception is DiagnosticsException or IOException or UnauthorizedAccessException)
            {
                throw RecordWriteFailure(exception);
            }
        }
        finally
        {
            _writer.Release();
        }
    }

    private async Task AppendWithBoundaryHookAsync(
        ExecutionSampleRecord sample,
        CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var pendingAppend = WriteSample(sample);
                await _beforeBoundaryPublishAsync!().ConfigureAwait(false);
                PublishAppend(pendingAppend);
            }
            catch (Exception exception) when (
                exception is DiagnosticsException or IOException or UnauthorizedAccessException)
            {
                throw RecordWriteFailure(exception);
            }
        }
        finally
        {
            _writer.Release();
        }
    }

    private PendingExecutionSampleAppend WriteSample(ExecutionSampleRecord sample)
    {
        ThrowIfDisposing();
        ThrowIfWriteFailed();
        var normalizedSample = sample with { ObservedAtUtc = sample.ObservedAtUtc.ToUniversalTime() };
        var threadIndex = GetOrAddThreadIndex(normalizedSample.ThreadId);
        var segment = _currentSegment;
        var record = ArrayPool<byte>.Shared.Rent(32);
        try
        {
            var recordLength = EncodeRecord(
                normalizedSample,
                segment?.LastObservedAtUtc ?? normalizedSample.ObservedAtUtc,
                record,
                threadIndex);
            if (segment is null || (segment.DataLength > 0 && segment.DataLength + recordLength > _segmentDataLimitBytes))
            {
                if (segment is not null)
                {
                    SealSegment(segment);
                }

                segment = CreateSegment(normalizedSample.ObservedAtUtc);
                recordLength = EncodeRecord(
                    normalizedSample,
                    segment.LastObservedAtUtc,
                    record,
                    threadIndex);
            }

            WriteRecord(segment.Stream, record.AsSpan(0, recordLength));
            return new PendingExecutionSampleAppend(
                segment,
                normalizedSample,
                recordLength);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(record);
        }
    }

    private void PublishAppend(PendingExecutionSampleAppend pendingAppend)
    {
        lock (_stateLock)
        {
            var segment = pendingAppend.Segment;
            var sample = pendingAppend.Sample;
            segment.DataLength += pendingAppend.RecordLength;
            segment.RecordCount++;
            segment.StartedAtUtc = Min(segment.StartedAtUtc, sample.ObservedAtUtc);
            segment.EndedAtUtc = Max(segment.EndedAtUtc, sample.ObservedAtUtc);
            segment.LastObservedAtUtc = sample.ObservedAtUtc;
            _writtenThroughUtc = Max(_writtenThroughUtc, sample.ObservedAtUtc);
            _lastCompletedRecord++;
        }
    }

    private DiagnosticsException RecordWriteFailure(Exception exception)
    {
        var storageException = exception as DiagnosticsException
            ?? ExecutionCaptureStorageLayout.CreateStorageException("Execution sample could not be committed.", exception);
        lock (_stateLock)
        {
            _writeFailure ??= storageException;
        }

        return storageException;
    }

    /// <summary>
    /// 捕获不会包含后续追加记录的读取上界。
    /// </summary>
    public ExecutionCaptureReadBoundary CaptureReadBoundary()
    {
        lock (_stateLock)
        {
            ThrowIfDisposingLocked();
            if (_writeFailure is not null)
            {
                throw _writeFailure;
            }

            try
            {
                foreach (var segment in _segments)
                {
                    FlushSegment(segment);
                }
            }
            catch (DiagnosticsException exception)
            {
                throw RecordWriteFailure(exception);
            }

            return new ExecutionCaptureReadBoundary(_startedAtUtc, _writtenThroughUtc, _lastCompletedRecord)
            {
                SegmentReadLimits = _segments
                    .Where(segment => segment.RecordCount > 0)
                    .Select(segment => new ExecutionCaptureSegmentReadLimit(
                        segment.Path,
                        segment.StartedAtUtc,
                        segment.EndedAtUtc,
                        segment.DataLength,
                        segment.RecordCount))
                    .ToArray()
            };
        }
    }

    /// <summary>
    /// 按半开 UTC 时间范围异步读取指定边界内的样本。
    /// </summary>
    public async IAsyncEnumerable<ExecutionSampleRecord> ReadAsync(
        ExecutionTimeRange range,
        ExecutionCaptureReadBoundary boundary,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(boundary);
        EnterReader(cancellationToken);
        try
        {
            var threadIdsByIndex = GetThreadIdsSnapshot();
            foreach (var segment in GetIntersectingSegments(range, boundary))
            {
                await using var stream = OpenSegmentForRead(segment.Path);
                var header = await ReadSegmentHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
                var reader = new ExecutionCaptureSegmentReader(stream, segment.DataLength);
                var previousObservedAtUtc = header.AnchorUtc;
                for (long recordIndex = 0; recordIndex < segment.RecordCount; recordIndex++)
                {
                    await reader.EnsureRecordDataAsync(cancellationToken).ConfigureAwait(false);
                    var sample = ReadRecord(
                        reader,
                        previousObservedAtUtc,
                        threadIdsByIndex,
                        cancellationToken);
                    previousObservedAtUtc = sample.ObservedAtUtc;
                    if (sample.ObservedAtUtc >= range.StartAtUtc && sample.ObservedAtUtc < range.EndAtUtc)
                    {
                        yield return sample;
                    }
                }

                if (reader.RemainingBytes != 0)
                {
                    throw ExecutionCaptureStorageLayout.CreateStorageException(
                        "Execution capture segment length does not match its completed record boundary.",
                        new InvalidDataException("Segment contains incomplete or unexpected record bytes."));
                }
            }
        }
        finally
        {
            ExitReader();
        }
    }

    /// <summary>
    /// 按首次出现顺序读取指定边界内各调用栈的样本计数。
    /// </summary>
    public async Task<ExecutionStackSampleCounts> ReadStackSampleCountsAsync(
        ExecutionTimeRange range,
        ExecutionCaptureReadBoundary boundary,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(boundary);
        EnterReader(cancellationToken);
        try
        {
            var threadIdsByIndex = GetThreadIdsSnapshot();
            var countsByStackId = new Dictionary<int, long>();
            var stackIdsInFirstSeenOrder = new List<int>();
            long receivedSampleCount = 0;
            foreach (var segment in GetIntersectingSegments(range, boundary))
            {
                await using var stream = OpenSegmentForRead(segment.Path);
                var header = await ReadSegmentHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
                var reader = new ExecutionCaptureSegmentReader(stream, segment.DataLength);
                var previousObservedAtUtc = header.AnchorUtc;
                for (long recordIndex = 0; recordIndex < segment.RecordCount; recordIndex++)
                {
                    await reader.EnsureRecordDataAsync(cancellationToken).ConfigureAwait(false);
                    var sample = ReadRecord(
                        reader,
                        previousObservedAtUtc,
                        threadIdsByIndex,
                        cancellationToken);
                    previousObservedAtUtc = sample.ObservedAtUtc;
                    if (sample.ObservedAtUtc < range.StartAtUtc || sample.ObservedAtUtc >= range.EndAtUtc)
                    {
                        continue;
                    }

                    receivedSampleCount = checked(receivedSampleCount + 1);
                    ref var stackSampleCount = ref CollectionsMarshal.GetValueRefOrAddDefault(
                        countsByStackId,
                        sample.StackId,
                        out var exists);
                    if (!exists)
                    {
                        stackIdsInFirstSeenOrder.Add(sample.StackId);
                    }

                    stackSampleCount = checked(stackSampleCount + 1);
                }

                if (reader.RemainingBytes != 0)
                {
                    throw ExecutionCaptureStorageLayout.CreateStorageException(
                        "Execution capture segment length does not match its completed record boundary.",
                        new InvalidDataException("Segment contains incomplete or unexpected record bytes."));
                }
            }

            var stacks = new ExecutionStackSampleCount[stackIdsInFirstSeenOrder.Count];
            for (var index = 0; index < stackIdsInFirstSeenOrder.Count; index++)
            {
                var stackId = stackIdsInFirstSeenOrder[index];
                stacks[index] = new ExecutionStackSampleCount(stackId, countsByStackId[stackId]);
            }

            return new ExecutionStackSampleCounts(receivedSampleCount, stacks);
        }
        finally
        {
            ExitReader();
        }
    }

    /// <summary>
    /// 拒绝新读取，等待已登记读取完成后清理本会话目录。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposeStarted = true;
            _readersDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_activeReaderCount == 0)
            {
                _readersDrained.TrySetResult();
            }

            _disposeTask = DisposeCoreAsync(_readersDrained.Task);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task readersDrained)
    {
        try
        {
            await _writer.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_afterDisposeWriterAcquiredAsync is not null)
                {
                    await _afterDisposeWriterAcquiredAsync().ConfigureAwait(false);
                }

                await readersDrained.ConfigureAwait(false);
                if (_currentSegment is not null)
                {
                    SealSegment(_currentSegment);
                }

                foreach (var segment in _segments)
                {
                    await segment.Stream.DisposeAsync().ConfigureAwait(false);
                }

                _currentSegment = null;

                try
                {
                    Directory.Delete(_layout.SessionDirectory, recursive: true);
                }
                catch (DirectoryNotFoundException)
                {
                }
            }
            finally
            {
                _writer.Release();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw ExecutionCaptureStorageLayout.CreateStorageException("Execution capture storage could not be disposed.", exception);
        }
    }

    private ExecutionCaptureSegment CreateSegment(DateTimeOffset anchorUtc)
    {
        try
        {
            var segment = new ExecutionCaptureSegment(
                _segments.Count,
                _layout.GetSegmentPath(_segments.Count),
                anchorUtc.ToUniversalTime());
            segment.Stream = new FileStream(
                segment.Path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                options: FileOptions.SequentialScan);
            WriteSegmentHeader(segment);
            segment.Stream.Position = SegmentHeaderLength;
            lock (_stateLock)
            {
                _segments.Add(segment);
            }

            _currentSegment = segment;
            return segment;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ExecutionCaptureStorageLayout.CreateStorageException("Execution capture segment could not be created.", exception);
        }
    }

    private void SealSegment(ExecutionCaptureSegment segment)
    {
        if (segment.IsSealed)
        {
            return;
        }

        FlushSegment(segment);
        segment.IsSealed = true;
    }

    private static void WriteRecord(FileStream stream, ReadOnlySpan<byte> record)
    {
        try
        {
            lock (stream)
            {
                stream.Write(record);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ExecutionCaptureStorageLayout.CreateStorageException("Execution sample could not be written.", exception);
        }
    }

    private void FlushSegment(ExecutionCaptureSegment segment)
    {
        try
        {
            _beforeSegmentFlush?.Invoke();
            lock (segment.Stream)
            {
                segment.Stream.Flush();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ExecutionCaptureStorageLayout.CreateStorageException(
                "Execution sample could not be committed.",
                exception);
        }
    }

    private static void WriteSegmentHeader(ExecutionCaptureSegment segment)
    {
        try
        {
            var position = segment.Stream.Position;
            segment.Stream.Position = 0;
            Span<byte> header = stackalloc byte[SegmentHeaderLength];
            SegmentMagic.CopyTo(header);
            BitConverter.TryWriteBytes(header.Slice(4, sizeof(int)), SegmentFormatVersion);
            BitConverter.TryWriteBytes(header.Slice(8, sizeof(long)), UtcTicks(segment.AnchorUtc));
            BitConverter.TryWriteBytes(header.Slice(16, sizeof(long)), UtcTicks(segment.StartedAtUtc));
            BitConverter.TryWriteBytes(header.Slice(24, sizeof(long)), UtcTicks(segment.EndedAtUtc));
            BitConverter.TryWriteBytes(header.Slice(32, sizeof(long)), segment.RecordCount);
            BitConverter.TryWriteBytes(header.Slice(40, sizeof(long)), segment.DataLength);
            segment.Stream.Write(header);
            segment.Stream.Position = position;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ExecutionCaptureStorageLayout.CreateStorageException("Execution capture segment header could not be written.", exception);
        }
    }

    private static FileStream OpenSegmentForRead(string path)
    {
        try
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ExecutionCaptureStorageLayout.CreateStorageException("Execution capture segment could not be opened for reading.", exception);
        }
    }

    private static async Task<ExecutionCaptureSegmentHeader> ReadSegmentHeaderAsync(FileStream stream, CancellationToken cancellationToken)
    {
        try
        {
            var header = new byte[SegmentHeaderLength];
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            if (!header.AsSpan(0, SegmentMagic.Length).SequenceEqual(SegmentMagic)
                || BitConverter.ToInt32(header, 4) != SegmentFormatVersion)
            {
                throw new InvalidDataException("Execution capture segment header is invalid.");
            }

            var anchorUtc = new DateTimeOffset(new DateTime(BitConverter.ToInt64(header, 8), DateTimeKind.Utc));
            var startedAtUtc = new DateTimeOffset(new DateTime(BitConverter.ToInt64(header, 16), DateTimeKind.Utc));
            var endedAtUtc = new DateTimeOffset(new DateTime(BitConverter.ToInt64(header, 24), DateTimeKind.Utc));
            var recordCount = BitConverter.ToInt64(header, 32);
            var dataLength = BitConverter.ToInt64(header, 40);
            if (recordCount < 0 || dataLength < 0 || endedAtUtc < startedAtUtc)
            {
                throw new InvalidDataException("Execution capture segment metadata is invalid.");
            }

            return new ExecutionCaptureSegmentHeader(anchorUtc, startedAtUtc, endedAtUtc, recordCount, dataLength);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentOutOfRangeException)
        {
            throw ExecutionCaptureStorageLayout.CreateStorageException("Execution capture segment format is invalid.", exception);
        }
    }

    private static ExecutionSampleRecord ReadRecord(
        ExecutionCaptureSegmentReader reader,
        DateTimeOffset previousObservedAtUtc,
        int[] threadIdsByIndex,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timestampDeltaTicks = ReadSigned7Bit(reader);
            var stackId = ReadInt32(reader);
            var threadIndex = ReadInt32(reader);
            if ((uint)threadIndex >= (uint)threadIdsByIndex.Length)
            {
                throw new InvalidDataException("Execution capture thread index is not known by this store.");
            }

            var threadId = threadIdsByIndex[threadIndex];
            var observedAtUtc = previousObservedAtUtc.AddTicks(timestampDeltaTicks);
            return new ExecutionSampleRecord(observedAtUtc, threadId, stackId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            throw ExecutionCaptureStorageLayout.CreateStorageException("Execution capture sample record is invalid.", exception);
        }
    }

    private static IEnumerable<ExecutionCaptureSegmentReadLimit> GetIntersectingSegments(
        ExecutionTimeRange range,
        ExecutionCaptureReadBoundary boundary)
    {
        if (boundary.LastCompletedRecord < -1 || boundary.SegmentReadLimits is null)
        {
            throw ExecutionCaptureStorageLayout.CreateStorageException(
                "Execution capture read boundary is invalid.",
                new InvalidDataException("Read boundary does not contain a valid completed-record limit."));
        }

        return boundary.SegmentReadLimits.Where(segment =>
            segment.EndedAtUtc >= range.StartAtUtc
            && segment.StartedAtUtc < range.EndAtUtc);
    }

    private void EnterReader(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateLock)
        {
            ThrowIfDisposingLocked();
            _activeReaderCount++;
        }
    }

    private void ExitReader()
    {
        TaskCompletionSource? readersDrained = null;
        lock (_stateLock)
        {
            _activeReaderCount--;
            if (_disposeStarted && _activeReaderCount == 0)
            {
                readersDrained = _readersDrained;
            }
        }

        readersDrained?.TrySetResult();
    }

    private void ThrowIfDisposing()
    {
        lock (_stateLock)
        {
            ThrowIfDisposingLocked();
        }
    }

    private void ThrowIfWriteFailed()
    {
        lock (_stateLock)
        {
            if (_writeFailure is not null)
            {
                throw _writeFailure;
            }
        }
    }

    private void ThrowIfDisposingLocked()
    {
        ObjectDisposedException.ThrowIf(_disposeStarted, this);
    }

    private int GetOrAddThreadIndex(int threadId)
    {
        lock (_stateLock)
        {
            ThrowIfDisposingLocked();
            if (_threadIndexes.TryGetValue(threadId, out var existingIndex))
            {
                return existingIndex;
            }

            var threadIndex = _threadIdsByIndex.Count;
            _threadIndexes.Add(threadId, threadIndex);
            _threadIdsByIndex.Add(threadId);
            return threadIndex;
        }
    }

    private int[] GetThreadIdsSnapshot()
    {
        lock (_stateLock)
        {
            return _threadIdsByIndex.ToArray();
        }
    }

    private static int EncodeRecord(
        ExecutionSampleRecord sample,
        DateTimeOffset anchorUtc,
        Span<byte> destination,
        int threadIndex)
    {
        var offset = 0;
        WriteSigned7Bit(destination, ref offset, checked(UtcTicks(sample.ObservedAtUtc) - UtcTicks(anchorUtc)));
        WriteUnsigned7Bit(destination, ref offset, (uint)sample.StackId);
        WriteUnsigned7Bit(destination, ref offset, (uint)threadIndex);
        return offset;
    }

    private static void WriteSigned7Bit(Span<byte> destination, ref int offset, long value)
    {
        var encoded = unchecked((ulong)((value << 1) ^ (value >> 63)));
        WriteUnsigned7Bit(destination, ref offset, encoded);
    }

    private static void WriteUnsigned7Bit(Span<byte> destination, ref int offset, ulong value)
    {
        while (value >= 0x80)
        {
            destination[offset++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[offset++] = (byte)value;
    }

    private static long ReadSigned7Bit(ExecutionCaptureSegmentReader reader)
    {
        var encoded = ReadUnsigned7Bit(reader);
        var value = (long)(encoded >> 1);
        return (encoded & 1) == 0 ? value : ~value;
    }

    private static int ReadInt32(ExecutionCaptureSegmentReader reader)
    {
        var value = ReadUnsigned7Bit(reader);
        if (value > int.MaxValue)
        {
            throw new InvalidDataException("Execution capture identifier exceeds Int32 range.");
        }

        return (int)value;
    }

    private static ulong ReadUnsigned7Bit(ExecutionCaptureSegmentReader reader)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var next = reader.ReadByte();
            if (shift == 63 && (next & 0xFE) != 0)
            {
                throw new InvalidDataException("Execution capture variable-length integer overflows UInt64.");
            }

            value |= (ulong)(next & 0x7F) << shift;
            if ((next & 0x80) == 0)
            {
                return value;
            }
        }

        throw new InvalidDataException("Execution capture variable-length integer is too long.");
    }

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) => first <= second ? first : second;

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) => first >= second ? first : second;

    private static long UtcTicks(DateTimeOffset value) => value.UtcDateTime.Ticks;

    private readonly record struct PendingExecutionSampleAppend(
        ExecutionCaptureSegment Segment,
        ExecutionSampleRecord Sample,
        int RecordLength);

    private readonly record struct ExecutionStackKey(int ParentStackId, int FrameId);

    private sealed class ExecutionCaptureSegment
    {
        public ExecutionCaptureSegment(int number, string path, DateTimeOffset anchorUtc)
        {
            Number = number;
            Path = path;
            AnchorUtc = anchorUtc;
            StartedAtUtc = anchorUtc;
            EndedAtUtc = anchorUtc;
            LastObservedAtUtc = anchorUtc;
        }

        public int Number { get; }

        public string Path { get; }

        public DateTimeOffset AnchorUtc { get; }

        public DateTimeOffset StartedAtUtc { get; set; }

        public DateTimeOffset EndedAtUtc { get; set; }

        public DateTimeOffset LastObservedAtUtc { get; set; }

        public long RecordCount { get; set; }

        public long DataLength { get; set; }

        public bool IsSealed { get; set; }

        public FileStream Stream { get; set; } = null!;
    }

    private sealed class ExecutionCaptureSegmentReader
    {
        private readonly FileStream _stream;
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _bufferOffset;
        private int _bufferLength;

        public ExecutionCaptureSegmentReader(FileStream stream, long remainingBytes)
        {
            _stream = stream;
            RemainingBytes = remainingBytes;
        }

        public long RemainingBytes { get; private set; }

        public ValueTask EnsureRecordDataAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RemainingBytes == 0)
            {
                throw ExecutionCaptureStorageLayout.CreateStorageException(
                    "Execution capture sample record is invalid.",
                    new InvalidDataException("Execution capture record exceeds its completed byte boundary."));
            }

            var requiredByteCount = (int)Math.Min(MaximumEncodedRecordLength, RemainingBytes);
            if (_bufferLength - _bufferOffset >= requiredByteCount)
            {
                return ValueTask.CompletedTask;
            }

            return FillBufferAsync(requiredByteCount, cancellationToken);
        }

        public byte ReadByte()
        {
            if (RemainingBytes == 0 || _bufferOffset == _bufferLength)
            {
                throw new InvalidDataException("Execution capture record exceeds its completed byte boundary.");
            }

            RemainingBytes--;
            return _buffer[_bufferOffset++];
        }

        private async ValueTask FillBufferAsync(int requiredByteCount, CancellationToken cancellationToken)
        {
            try
            {
                var availableByteCount = _bufferLength - _bufferOffset;
                if (availableByteCount > 0)
                {
                    _buffer.AsSpan(_bufferOffset, availableByteCount).CopyTo(_buffer);
                }

                _bufferOffset = 0;
                _bufferLength = availableByteCount;
                while (_bufferLength < requiredByteCount)
                {
                    var remainingUnbufferedBytes = RemainingBytes - _bufferLength;
                    var maximumReadLength = (int)Math.Min(
                        _buffer.Length - _bufferLength,
                        remainingUnbufferedBytes);
                    var bytesRead = await _stream.ReadAsync(
                        _buffer.AsMemory(_bufferLength, maximumReadLength),
                        cancellationToken).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        throw new EndOfStreamException(
                            "Execution capture segment ended before its completed byte boundary.");
                    }

                    _bufferLength += bytesRead;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentOutOfRangeException)
            {
                throw ExecutionCaptureStorageLayout.CreateStorageException(
                    "Execution capture sample record is invalid.",
                    exception);
            }
        }
    }
}

internal sealed record ExecutionCaptureSegmentReadLimit(
    string Path,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    long DataLength,
    long RecordCount);

internal sealed record ExecutionCaptureSegmentHeader(
    DateTimeOffset AnchorUtc,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    long RecordCount,
    long DataLength);
