using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 表示已归一化到 UTC、可写入执行采样分段的一条样本。
/// </summary>
internal readonly record struct ExecutionSampleRecord(
    DateTimeOffset ObservedAtUtc,
    int ThreadId,
    int StackId);

/// <summary>
/// 表示一个调用栈在固定读取边界内累计得到的样本数。
/// </summary>
internal readonly record struct ExecutionStackSampleCount(
    int StackId,
    long SampleCount);

/// <summary>
/// 保存按首次出现顺序排列的调用栈计数及其总样本数。
/// </summary>
internal sealed record ExecutionStackSampleCounts(
    long ReceivedSampleCount,
    IReadOnlyList<ExecutionStackSampleCount> Stacks);

/// <summary>
/// 描述可跨样本去重的托管调用帧元数据。
/// </summary>
internal readonly record struct ExecutionFrameDescriptor(
    string MethodName,
    string? ModuleName,
    string? ModulePath,
    string SymbolKey);

/// <summary>
/// 将会话内帧标识关联到帧描述符和可选运行时符号地址。
/// </summary>
internal sealed record ExecutionFrameReference(
    int FrameId,
    ExecutionFrameDescriptor Descriptor,
    TraceCodeAddress? SymbolAddress);

/// <summary>
/// 冻结一次读取可见的全局水位和各分段完成边界，避免并发追加改变查询结果。
/// </summary>
internal sealed record ExecutionCaptureReadBoundary(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset WrittenThroughUtc,
    long LastCompletedRecord)
{
    internal IReadOnlyList<ExecutionCaptureSegmentReadLimit> SegmentReadLimits { get; init; } = [];
}

/// <summary>
/// 标识封存段及其完成记录数，用于判断历史范围缓存是否仍然有效。
/// </summary>
internal readonly record struct ExecutionCaptureSealedSegmentVersion(
    int SegmentNumber,
    long CompletedRecordCount);

/// <summary>
/// 描述查询范围依赖的封存段版本以及是否涉及仍会变化的活动段。
/// </summary>
internal sealed record ExecutionCaptureRangeDependency(
    bool HasActiveSegment,
    IReadOnlyList<ExecutionCaptureSealedSegmentVersion> SealedSegmentVersions)
{
    internal bool IsSealedHistoryOnly => !HasActiveSegment && SealedSegmentVersions.Count > 0;
}

/// <summary>
/// 为单个诊断会话追加、读取并在销毁时清理执行采样数据。
/// </summary>
internal sealed class ExecutionCaptureStore : IAsyncDisposable
{
    private const int SegmentHeaderLength = 48;
    private const int SummaryHeaderLength = 16;
    private const int SummaryEntryLength = sizeof(int) + sizeof(long);
    private const int DefaultSegmentDataLimitBytes = 4 * 1024 * 1024;
    private const int MaximumEncodedRecordLength = 30;
    private const int SegmentFormatVersion = 3;
    private const int SummaryFormatVersion = 1;
    private static ReadOnlySpan<byte> SegmentMagic => "ECS1"u8;
    private static ReadOnlySpan<byte> SummaryMagic => "ECSU"u8;

    private readonly ExecutionCaptureStorageLayout _layout;
    private readonly int _segmentDataLimitBytes;
    private readonly Func<Task>? _beforeBoundaryPublishAsync;
    private readonly Func<Task>? _beforeStackFramesReadAsync;
    private readonly Func<CancellationToken, Task>? _beforeStackFramesReadWithCancellationAsync;
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
        Func<CancellationToken, Task>? beforeStackFramesReadWithCancellationAsync = null,
        Func<Task>? afterDisposeWriterAcquiredAsync = null,
        Action? beforeSegmentFlush = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentDataLimitBytes, 3);

        _segmentDataLimitBytes = segmentDataLimitBytes;
        _beforeBoundaryPublishAsync = beforeBoundaryPublishAsync;
        _beforeStackFramesReadAsync = beforeStackFramesReadAsync;
        _beforeStackFramesReadWithCancellationAsync = beforeStackFramesReadWithCancellationAsync;
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

        if (_beforeStackFramesReadWithCancellationAsync is not null)
        {
            await _beforeStackFramesReadWithCancellationAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// 在取得写入闸门前拒绝无法编码的线程或调用栈标识。
    /// </summary>
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

    /// <summary>
    /// 在没有测试边界钩子时同步持有写入闸门，保证记录写入和可见边界原子发布。
    /// </summary>
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

    /// <summary>
    /// 为并发边界测试在物理写入与逻辑发布之间插入异步钩子，生产语义仍保持原子发布。
    /// </summary>
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

    /// <summary>
    /// 将样本编码到适当分段；在分段容量达到上限时先封存旧段再创建新段。
    /// </summary>
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

    /// <summary>
    /// 在状态锁内发布已落盘记录，并同步推进读取水位与封存摘要所需的聚合状态。
    /// </summary>
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
            ref var summarySampleCount = ref CollectionsMarshal.GetValueRefOrAddDefault(
                segment.StackSampleCounts,
                sample.StackId,
                out var stackAlreadyObserved);
            if (!stackAlreadyObserved)
            {
                segment.StackIdsInFirstSeenOrder.Add(sample.StackId);
            }

            summarySampleCount = checked(summarySampleCount + 1);
            _writtenThroughUtc = Max(_writtenThroughUtc, sample.ObservedAtUtc);
            _lastCompletedRecord++;
        }
    }

    /// <summary>
    /// 将首个不可恢复写入故障固定为会话状态，避免后续读取或追加观察到不一致的存储。
    /// </summary>
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
                        segment.Number,
                        segment.Path,
                        segment.SummaryPath,
                        segment.StartedAtUtc,
                        segment.EndedAtUtc,
                        segment.DataLength,
                        segment.RecordCount,
                        segment.IsSealed))
                    .ToArray()
            };
        }
    }

    /// <summary>
    /// 获取指定读取边界和时间范围所依赖的封存段版本及活动段状态。
    /// </summary>
    /// <param name="range">要查询的半开 UTC 时间范围。</param>
    /// <param name="boundary">调用方已固定的读取边界。</param>
    /// <returns>可用于会话内缓存有效性判断的范围依赖。</returns>
    internal static ExecutionCaptureRangeDependency GetRangeDependency(
        ExecutionTimeRange range,
        ExecutionCaptureReadBoundary boundary)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(boundary);

        var sealedSegmentVersions = new List<ExecutionCaptureSealedSegmentVersion>();
        var hasActiveSegment = false;
        foreach (var segment in GetIntersectingSegments(range, boundary))
        {
            if (segment.IsSealed)
            {
                sealedSegmentVersions.Add(new ExecutionCaptureSealedSegmentVersion(
                    segment.SegmentNumber,
                    segment.RecordCount));
            }
            else
            {
                hasActiveSegment = true;
            }
        }

        return new ExecutionCaptureRangeDependency(hasActiveSegment, sealedSegmentVersions);
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
    /// 判断查询范围是否完整覆盖封存段，从而可以直接合并其摘要而无需逐条解码。
    /// </summary>
    private static bool IsFullyContainedSealedSegment(
        ExecutionCaptureSegmentReadLimit segment,
        ExecutionTimeRange range) =>
        segment.IsSealed
        && segment.StartedAtUtc >= range.StartAtUtc
        && segment.EndedAtUtc < range.EndAtUtc;

    /// <summary>
    /// 将一个分段摘要合并到查询累加器，并校验摘要总数没有与分项计数脱节。
    /// </summary>
    private static void MergeStackSampleCounts(
        ExecutionStackSampleCounts source,
        Dictionary<int, long> countsByStackId,
        List<int> stackIdsInFirstSeenOrder,
        ref long receivedSampleCount)
    {
        foreach (var stack in source.Stacks)
        {
            MergeStackSampleCount(
                stack,
                countsByStackId,
                stackIdsInFirstSeenOrder,
                ref receivedSampleCount);
        }

        if (source.ReceivedSampleCount != source.Stacks.Sum(static stack => stack.SampleCount))
        {
            throw ExecutionCaptureStorageLayout.CreateStorageException(
                "Execution capture segment summary count is invalid.",
                new InvalidDataException("Summary total sample count does not match its stack counts."));
        }
    }

    /// <summary>
    /// 合并单个调用栈计数，同时保留调用栈首次进入查询范围的稳定排序。
    /// </summary>
    private static void MergeStackSampleCount(
        ExecutionStackSampleCount source,
        Dictionary<int, long> countsByStackId,
        List<int> stackIdsInFirstSeenOrder,
        ref long receivedSampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(source.StackId);
        ArgumentOutOfRangeException.ThrowIfNegative(source.SampleCount);
        if (source.SampleCount == 0)
        {
            return;
        }

        receivedSampleCount = checked(receivedSampleCount + source.SampleCount);
        ref var stackSampleCount = ref CollectionsMarshal.GetValueRefOrAddDefault(
            countsByStackId,
            source.StackId,
            out var exists);
        if (!exists)
        {
            stackIdsInFirstSeenOrder.Add(source.StackId);
        }

        stackSampleCount = checked(stackSampleCount + source.SampleCount);
    }

    /// <summary>
    /// 按首次出现顺序读取指定边界内各调用栈的样本计数。
    /// </summary>
    public async Task<ExecutionStackSampleCounts> ReadStackSampleCountsAsync(
        ExecutionTimeRange range,
        ExecutionCaptureReadBoundary boundary,
        CancellationToken cancellationToken) => await ReadStackSampleCountsCoreAsync(
            range,
            boundary,
            useIncrementalSummaries: false,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 合并完整封存段的持久化摘要，并逐条读取边界或活动段内各调用栈的样本计数。
    /// </summary>
    /// <param name="range">要读取的半开 UTC 时间范围。</param>
    /// <param name="boundary">调用方已固定的读取边界。</param>
    /// <param name="cancellationToken">取消读取和摘要校验的令牌。</param>
    /// <returns>按首次出现顺序排列的调用栈样本计数。</returns>
    public async Task<ExecutionStackSampleCounts> ReadIncrementalStackSampleCountsAsync(
        ExecutionTimeRange range,
        ExecutionCaptureReadBoundary boundary,
        CancellationToken cancellationToken) => await ReadStackSampleCountsCoreAsync(
            range,
            boundary,
            useIncrementalSummaries: true,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 在登记读取生命周期内统一执行全量或增量计数；完整封存段可使用摘要，边界段必须逐条读取。
    /// </summary>
    private async Task<ExecutionStackSampleCounts> ReadStackSampleCountsCoreAsync(
        ExecutionTimeRange range,
        ExecutionCaptureReadBoundary boundary,
        bool useIncrementalSummaries,
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
                if (useIncrementalSummaries && IsFullyContainedSealedSegment(segment, range))
                {
                    var summary = await ReadSegmentSummaryAsync(
                        segment.SummaryPath,
                        segment.RecordCount,
                        cancellationToken).ConfigureAwait(false);
                    MergeStackSampleCounts(
                        summary,
                        countsByStackId,
                        stackIdsInFirstSeenOrder,
                        ref receivedSampleCount);
                    continue;
                }

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

                    MergeStackSampleCount(
                        new ExecutionStackSampleCount(sample.StackId, 1),
                        countsByStackId,
                        stackIdsInFirstSeenOrder,
                        ref receivedSampleCount);
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

    /// <summary>
    /// 等待写入和已登记读取收敛后封存最后分段、关闭流并删除会话私有目录。
    /// </summary>
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
                if (_currentSegment is not null && _writeFailure is null)
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

    /// <summary>
    /// 创建带格式头的新分段，并把它注册为后续追加的活动分段。
    /// </summary>
    private ExecutionCaptureSegment CreateSegment(DateTimeOffset anchorUtc)
    {
        try
        {
            var segment = new ExecutionCaptureSegment(
                _segments.Count,
                _layout.GetSegmentPath(_segments.Count),
                _layout.GetSegmentSummaryPath(_segments.Count),
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

    /// <summary>
    /// 将已满或会话结束的分段刷盘、写入摘要，并只在摘要持久化成功后释放重复的内存聚合状态。
    /// </summary>
    private void SealSegment(ExecutionCaptureSegment segment)
    {
        if (segment.IsSealed)
        {
            return;
        }

        FlushSegment(segment);
        WriteSegmentSummary(segment);
        segment.StackSampleCounts.Clear();
        segment.StackIdsInFirstSeenOrder.Clear();
        segment.IsSealed = true;
    }

    /// <summary>
    /// 将封存段内按首次出现顺序聚合的调用栈计数写为可校验的持久化摘要。
    /// </summary>
    private static void WriteSegmentSummary(ExecutionCaptureSegment segment)
    {
        try
        {
            using var stream = new FileStream(
                segment.SummaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 64 * 1024,
                options: FileOptions.SequentialScan);
            Span<byte> header = stackalloc byte[SummaryHeaderLength];
            SummaryMagic.CopyTo(header);
            BitConverter.TryWriteBytes(header.Slice(4, sizeof(int)), SummaryFormatVersion);
            BitConverter.TryWriteBytes(header.Slice(8, sizeof(int)), segment.StackIdsInFirstSeenOrder.Count);
            BitConverter.TryWriteBytes(header.Slice(12, sizeof(int)), 0);
            stream.Write(header);

            Span<byte> entry = stackalloc byte[SummaryEntryLength];
            foreach (var stackId in segment.StackIdsInFirstSeenOrder)
            {
                BitConverter.TryWriteBytes(entry.Slice(0, sizeof(int)), stackId);
                BitConverter.TryWriteBytes(entry.Slice(sizeof(int), sizeof(long)), segment.StackSampleCounts[stackId]);
                stream.Write(entry);
            }

            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ExecutionCaptureStorageLayout.CreateStorageException(
                "Execution capture segment summary could not be persisted.",
                exception);
        }
    }

    /// <summary>
    /// 读取并严格校验封存段摘要；在分配条目数组前先验证声明长度与文件剩余字节完全一致。
    /// </summary>
    private static async Task<ExecutionStackSampleCounts> ReadSegmentSummaryAsync(
        string summaryPath,
        long expectedRecordCount,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                summaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            var header = new byte[SummaryHeaderLength];
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            if (!header.AsSpan(0, SummaryMagic.Length).SequenceEqual(SummaryMagic)
                || BitConverter.ToInt32(header, 4) != SummaryFormatVersion)
            {
                throw new InvalidDataException("Execution capture segment summary header is invalid.");
            }

            var entryCount = BitConverter.ToInt32(header, 8);
            if (entryCount < 0 || BitConverter.ToInt32(header, 12) != 0)
            {
                throw new InvalidDataException("Execution capture segment summary metadata is invalid.");
            }

            var expectedEntryBytes = checked((long)entryCount * SummaryEntryLength);
            if (stream.Length - stream.Position != expectedEntryBytes)
            {
                throw new InvalidDataException("Execution capture segment summary length is invalid.");
            }

            var stacks = new ExecutionStackSampleCount[entryCount];
            var seenStackIds = new HashSet<int>();
            long receivedSampleCount = 0;
            var entry = new byte[SummaryEntryLength];
            for (var index = 0; index < stacks.Length; index++)
            {
                await stream.ReadExactlyAsync(entry, cancellationToken).ConfigureAwait(false);
                var stackId = BitConverter.ToInt32(entry, 0);
                var sampleCount = BitConverter.ToInt64(entry, sizeof(int));
                if (stackId < 0 || sampleCount <= 0 || !seenStackIds.Add(stackId))
                {
                    throw new InvalidDataException("Execution capture segment summary entries are invalid.");
                }

                stacks[index] = new ExecutionStackSampleCount(stackId, sampleCount);
                receivedSampleCount = checked(receivedSampleCount + sampleCount);
            }

            if (receivedSampleCount != expectedRecordCount)
            {
                throw new InvalidDataException("Execution capture segment summary does not match its segment boundary.");
            }

            return new ExecutionStackSampleCounts(receivedSampleCount, stacks);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException or OverflowException)
        {
            throw ExecutionCaptureStorageLayout.CreateStorageException(
                "Execution capture segment summary is invalid.",
                exception);
        }
    }

    /// <summary>
    /// 在分段流锁内写入已编码记录，并将底层 I/O 故障转换为稳定存储错误。
    /// </summary>
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

    /// <summary>
    /// 将分段已写入字节提交给读取边界；测试钩子在实际刷新前用于构造故障场景。
    /// </summary>
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

    /// <summary>
    /// 回写分段固定头中的时间范围、完成记录数和数据长度，使后续读者可验证边界。
    /// </summary>
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

    /// <summary>
    /// 以异步顺序读取方式打开分段，并统一映射文件访问失败。
    /// </summary>
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

    /// <summary>
    /// 读取并验证分段固定头，拒绝格式版本、长度或时间边界不可信的存储。
    /// </summary>
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

    /// <summary>
    /// 从分段字节流解码一条相对时间戳记录，并验证其线程索引和数值边界。
    /// </summary>
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

    /// <summary>
    /// 从冻结读取边界筛选与半开查询区间相交的分段，并先验证边界自身完整性。
    /// </summary>
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

    /// <summary>
    /// 登记一个读取者，阻止处置开始后出现新读取，并让处置流程能够等待既有读取完成。
    /// </summary>
    private void EnterReader(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateLock)
        {
            ThrowIfDisposingLocked();
            _activeReaderCount++;
        }
    }

    /// <summary>
    /// 注销读取者；最后一个读取者退出时唤醒正在等待清理的处置流程。
    /// </summary>
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

    /// <summary>
    /// 在状态锁保护下拒绝处置已开始的写入路径。
    /// </summary>
    private void ThrowIfDisposing()
    {
        lock (_stateLock)
        {
            ThrowIfDisposingLocked();
        }
    }

    /// <summary>
    /// 传播已固定的首个写入失败，避免继续向可能不一致的存储写入。
    /// </summary>
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

    /// <summary>
    /// 要求调用方已持有状态锁时检查处置状态。
    /// </summary>
    private void ThrowIfDisposingLocked()
    {
        ObjectDisposedException.ThrowIf(_disposeStarted, this);
    }

    /// <summary>
    /// 为运行时线程标识分配紧凑、会话内稳定的编码索引。
    /// </summary>
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

    /// <summary>
    /// 复制线程索引表，保证读取者不会枚举到并发追加正在修改的集合。
    /// </summary>
    private int[] GetThreadIdsSnapshot()
    {
        lock (_stateLock)
        {
            return _threadIdsByIndex.ToArray();
        }
    }

    /// <summary>
    /// 将时间差、调用栈标识和线程索引编码到调用方租用的紧凑缓冲区。
    /// </summary>
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

    /// <summary>
    /// 使用 ZigZag 加无符号 7 位变长编码写入有符号时间差。
    /// </summary>
    private static void WriteSigned7Bit(Span<byte> destination, ref int offset, long value)
    {
        var encoded = unchecked((ulong)((value << 1) ^ (value >> 63)));
        WriteUnsigned7Bit(destination, ref offset, encoded);
    }

    /// <summary>
    /// 将无符号值以 7 位变长格式写入目标缓冲区并推进偏移量。
    /// </summary>
    private static void WriteUnsigned7Bit(Span<byte> destination, ref int offset, ulong value)
    {
        while (value >= 0x80)
        {
            destination[offset++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[offset++] = (byte)value;
    }

    /// <summary>
    /// 读取无符号 7 位编码并还原 ZigZag 表示的有符号时间差。
    /// </summary>
    private static long ReadSigned7Bit(ExecutionCaptureSegmentReader reader)
    {
        var encoded = ReadUnsigned7Bit(reader);
        var value = (long)(encoded >> 1);
        return (encoded & 1) == 0 ? value : ~value;
    }

    /// <summary>
    /// 读取非负变长标识，并拒绝超过 <see cref="int.MaxValue"/> 的编码值。
    /// </summary>
    private static int ReadInt32(ExecutionCaptureSegmentReader reader)
    {
        var value = ReadUnsigned7Bit(reader);
        if (value > int.MaxValue)
        {
            throw new InvalidDataException("Execution capture identifier exceeds Int32 range.");
        }

        return (int)value;
    }

    /// <summary>
    /// 从受完成字节边界约束的读者读取无符号 7 位变长值，并检测溢出和过长编码。
    /// </summary>
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

    /// <summary>
    /// 返回两个 UTC 时间点中较早的值，用于扩展分段的观测下界。
    /// </summary>
    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) => first <= second ? first : second;

    /// <summary>
    /// 返回两个 UTC 时间点中较晚的值，用于推进分段或会话的观测上界。
    /// </summary>
    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) => first >= second ? first : second;

    /// <summary>
    /// 取得规范化 UTC 刻度，确保分段持久化和增量时间差不受原始偏移影响。
    /// </summary>
    private static long UtcTicks(DateTimeOffset value) => value.UtcDateTime.Ticks;

    /// <summary>
    /// 携带已经写入但尚未发布到读取边界的记录元数据。
    /// </summary>
    private readonly record struct PendingExecutionSampleAppend(
        ExecutionCaptureSegment Segment,
        ExecutionSampleRecord Sample,
        int RecordLength);

    /// <summary>
    /// 用父调用栈与当前帧组成调用树节点的去重键。
    /// </summary>
    private readonly record struct ExecutionStackKey(int ParentStackId, int FrameId);

    /// <summary>
    /// 保存一个物理分段的流、已发布边界和封存前的摘要聚合状态。
    /// </summary>
    private sealed class ExecutionCaptureSegment
    {
        /// <summary>
        /// 以首条样本时间初始化新分段的路径、时间锚点和空边界。
        /// </summary>
        public ExecutionCaptureSegment(
            int number,
            string path,
            string summaryPath,
            DateTimeOffset anchorUtc)
        {
            Number = number;
            Path = path;
            SummaryPath = summaryPath;
            AnchorUtc = anchorUtc;
            StartedAtUtc = anchorUtc;
            EndedAtUtc = anchorUtc;
            LastObservedAtUtc = anchorUtc;
        }

        public int Number { get; }

        public string Path { get; }

        public string SummaryPath { get; }

        public DateTimeOffset AnchorUtc { get; }

        public DateTimeOffset StartedAtUtc { get; set; }

        public DateTimeOffset EndedAtUtc { get; set; }

        public DateTimeOffset LastObservedAtUtc { get; set; }

        public long RecordCount { get; set; }

        public long DataLength { get; set; }

        public bool IsSealed { get; set; }

        public Dictionary<int, long> StackSampleCounts { get; } = [];

        public List<int> StackIdsInFirstSeenOrder { get; } = [];

        public FileStream Stream { get; set; } = null!;
    }

    /// <summary>
    /// 在不越过固定完成字节边界的前提下，为变长记录提供带缓冲的异步分段读取。
    /// </summary>
    private sealed class ExecutionCaptureSegmentReader
    {
        private readonly FileStream _stream;
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _bufferOffset;
        private int _bufferLength;

        /// <summary>
        /// 绑定分段流与调用方固定的可读取字节数，禁止读取并发追加的后续数据。
        /// </summary>
        public ExecutionCaptureSegmentReader(FileStream stream, long remainingBytes)
        {
            _stream = stream;
            RemainingBytes = remainingBytes;
        }

        public long RemainingBytes { get; private set; }

        /// <summary>
        /// 确保缓冲区包含一个最大编码记录所需的前导字节，或在固定边界前报告截断。
        /// </summary>
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

        /// <summary>
        /// 读取并消费一个已缓冲字节，同时推进固定完成边界的剩余计数。
        /// </summary>
        public byte ReadByte()
        {
            if (RemainingBytes == 0 || _bufferOffset == _bufferLength)
            {
                throw new InvalidDataException("Execution capture record exceeds its completed byte boundary.");
            }

            RemainingBytes--;
            return _buffer[_bufferOffset++];
        }

        /// <summary>
        /// 保留未消费前缀后异步补齐缓冲区，且绝不向流请求超过已发布边界的字节。
        /// </summary>
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

/// <summary>
/// 描述一次冻结读取可见的单个分段路径、时间范围、完成边界和封存状态。
/// </summary>
internal sealed record ExecutionCaptureSegmentReadLimit(
    int SegmentNumber,
    string Path,
    string SummaryPath,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    long DataLength,
    long RecordCount,
    bool IsSealed);

/// <summary>
/// 表示从物理分段固定头解析出的锚点、时间范围和完成数据边界。
/// </summary>
internal sealed record ExecutionCaptureSegmentHeader(
    DateTimeOffset AnchorUtc,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    long RecordCount,
    long DataLength);
