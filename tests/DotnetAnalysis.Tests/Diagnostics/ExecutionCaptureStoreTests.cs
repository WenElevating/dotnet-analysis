using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace DotnetAnalysis.Tests.Diagnostics;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class ExecutionCaptureStoreTests
{
    private static readonly TimeSpan ConcurrentTestTimeout = TimeSpan.FromSeconds(15);

    [TestMethod]
    public async Task GetOrAddFrameAndStackAsync_DeduplicatesEquivalentDescriptors()
    {
        await using var temporaryStore = CreateTemporaryStore();
        var store = temporaryStore.Store;
        var frame = new ExecutionFrameDescriptor("Worker.Run", "Worker", "C:\\Worker.dll", "0x123");

        var firstFrameId = await store.GetOrAddFrameAsync(frame, CancellationToken.None);
        var duplicateFrameId = await store.GetOrAddFrameAsync(frame, CancellationToken.None);
        var secondFrameId = await store.GetOrAddFrameAsync(
            frame with { SymbolKey = "0x456" },
            CancellationToken.None);
        var firstStackId = await store.GetOrAddStackAsync(-1, firstFrameId, CancellationToken.None);
        var duplicateStackId = await store.GetOrAddStackAsync(-1, firstFrameId, CancellationToken.None);
        var secondStackId = await store.GetOrAddStackAsync(firstStackId, secondFrameId, CancellationToken.None);

        Assert.AreEqual(firstFrameId, duplicateFrameId);
        Assert.AreNotEqual(firstFrameId, secondFrameId);
        Assert.AreEqual(firstStackId, duplicateStackId);
        Assert.AreNotEqual(firstStackId, secondStackId);
    }

    [TestMethod]
    public async Task ReadAsync_ReadsRecordsAcrossIntersectingSegments()
    {
        await using var temporaryStore = CreateTemporaryStore(segmentDataLimitBytes: 8);
        var store = temporaryStore.Store;
        await store.AppendAsync(Sample("00:00:01"), CancellationToken.None);
        await store.AppendAsync(Sample("00:00:02"), CancellationToken.None);
        await store.AppendAsync(Sample("00:00:03"), CancellationToken.None);
        var boundary = store.CaptureReadBoundary();

        var records = await store.ReadAsync(Range("00:00:01", "00:00:04"), boundary, CancellationToken.None).ToListAsync();

        Assert.HasCount(3, records);
    }

    [TestMethod]
    public async Task ReadAsync_UsesHalfOpenTimeRange()
    {
        await using var temporaryStore = CreateTemporaryStore();
        var store = temporaryStore.Store;
        await store.AppendAsync(Sample("00:00:01"), CancellationToken.None);
        await store.AppendAsync(Sample("00:00:02"), CancellationToken.None);
        await store.AppendAsync(Sample("00:00:03"), CancellationToken.None);
        var boundary = store.CaptureReadBoundary();

        var records = await store.ReadAsync(Range("00:00:01", "00:00:03"), boundary, CancellationToken.None).ToListAsync();

        Assert.HasCount(2, records);
        Assert.AreEqual(Sample("00:00:01").ObservedAtUtc, records[0].ObservedAtUtc);
        Assert.AreEqual(Sample("00:00:02").ObservedAtUtc, records[1].ObservedAtUtc);
    }

    [TestMethod]
    public async Task ReadStackSampleCountsAsync_AggregatesRangeInFirstSeenStackOrder()
    {
        await using var temporaryStore = CreateTemporaryStore();
        var store = temporaryStore.Store;
        await store.AppendAsync(Sample("00:00:01") with { StackId = 7 }, CancellationToken.None);
        await store.AppendAsync(Sample("00:00:02") with { StackId = 3 }, CancellationToken.None);
        await store.AppendAsync(Sample("00:00:03") with { StackId = 7 }, CancellationToken.None);
        await store.AppendAsync(Sample("00:00:04") with { StackId = 9 }, CancellationToken.None);
        var boundary = store.CaptureReadBoundary();

        var counts = await store.ReadStackSampleCountsAsync(
            Range("00:00:01", "00:00:04"),
            boundary,
            CancellationToken.None);

        Assert.AreEqual(3L, counts.ReceivedSampleCount);
        Assert.HasCount(2, counts.Stacks);
        Assert.AreEqual(new ExecutionStackSampleCount(StackId: 7, SampleCount: 2), counts.Stacks[0]);
        Assert.AreEqual(new ExecutionStackSampleCount(StackId: 3, SampleCount: 1), counts.Stacks[1]);
    }

    [TestMethod]
    public async Task ReadIncrementalStackSampleCountsAsync_MatchesFullScanAcrossSealedAndActiveSegments()
    {
        await using var temporaryStore = CreateTemporaryStore(segmentDataLimitBytes: 3);
        var store = temporaryStore.Store;
        await store.AppendAsync(Sample("00:00:03") with { StackId = 7 }, CancellationToken.None);
        await store.AppendAsync(Sample("00:00:01") with { StackId = 3 }, CancellationToken.None);
        await store.AppendAsync(Sample("00:00:04") with { StackId = 7 }, CancellationToken.None);
        await store.AppendAsync(Sample("00:00:02") with { StackId = 9 }, CancellationToken.None);
        var boundary = store.CaptureReadBoundary();
        var range = Range("00:00:01", "00:00:05");

        var fullScan = await store.ReadStackSampleCountsAsync(range, boundary, CancellationToken.None);
        var incremental = await store.ReadIncrementalStackSampleCountsAsync(range, boundary, CancellationToken.None);

        Assert.AreEqual(fullScan.ReceivedSampleCount, incremental.ReceivedSampleCount);
        CollectionAssert.AreEqual(fullScan.Stacks.ToArray(), incremental.Stacks.ToArray());
    }

    [TestMethod]
    public async Task ReadIncrementalStackSampleCountsAsync_WhenSealedSummaryIsCorrupt_ThrowsStorageFailureWithoutFallingBack()
    {
        await using var temporaryStore = CreateTemporaryStore(segmentDataLimitBytes: 3);
        var store = temporaryStore.Store;
        await store.AppendAsync(Sample("00:00:01") with { StackId = 3 }, CancellationToken.None);
        await store.AppendAsync(Sample("00:00:02") with { StackId = 7 }, CancellationToken.None);
        var boundary = store.CaptureReadBoundary();
        var sealedSegment = boundary.SegmentReadLimits.First(segment => segment.IsSealed);
        File.WriteAllBytes(sealedSegment.SummaryPath, [0x00]);
        var range = Range("00:00:00", "00:00:03");

        var failure = await Assert.ThrowsExactlyAsync<DiagnosticsException>(async () =>
            await store.ReadIncrementalStackSampleCountsAsync(range, boundary, CancellationToken.None));
        var fullScan = await store.ReadStackSampleCountsAsync(range, boundary, CancellationToken.None);

        Assert.AreEqual(DiagnosticsErrorCode.ExecutionProfileStorageFailed, failure.ErrorCode);
        Assert.AreEqual(2, fullScan.ReceivedSampleCount);
    }

    [TestMethod]
    public async Task ReadIncrementalStackSampleCountsAsync_WhenSummaryEntryCountExceedsFileLength_ThrowsStorageFailure()
    {
        await using var temporaryStore = CreateTemporaryStore(segmentDataLimitBytes: 3);
        var store = temporaryStore.Store;
        await store.AppendAsync(Sample("00:00:01") with { StackId = 3 }, CancellationToken.None);
        await store.AppendAsync(Sample("00:00:02") with { StackId = 7 }, CancellationToken.None);
        var boundary = store.CaptureReadBoundary();
        var sealedSegment = boundary.SegmentReadLimits.First(segment => segment.IsSealed);
        await using (var summary = new FileStream(
            sealedSegment.SummaryPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None))
        {
            summary.Position = 8;
            await summary.WriteAsync(BitConverter.GetBytes(int.MaxValue));
        }

        var failure = await Assert.ThrowsExactlyAsync<DiagnosticsException>(async () =>
            await store.ReadIncrementalStackSampleCountsAsync(
                Range("00:00:00", "00:00:03"),
                boundary,
                CancellationToken.None));

        Assert.AreEqual(DiagnosticsErrorCode.ExecutionProfileStorageFailed, failure.ErrorCode);
    }

    [TestMethod]
    public async Task AppendAsync_WhenSegmentSealed_ReleasesInMemorySummaryAfterPersisting()
    {
        await using var temporaryStore = CreateTemporaryStore(segmentDataLimitBytes: 3);
        var store = temporaryStore.Store;
        await store.AppendAsync(Sample("00:00:01") with { StackId = 3 }, CancellationToken.None);
        await store.AppendAsync(Sample("00:00:02") with { StackId = 7 }, CancellationToken.None);
        var boundary = store.CaptureReadBoundary();

        Assert.AreEqual(0, GetSealedSegmentSummaryCollectionCount(store, "StackSampleCounts"));
        Assert.AreEqual(0, GetSealedSegmentSummaryCollectionCount(store, "StackIdsInFirstSeenOrder"));

        var counts = await store.ReadIncrementalStackSampleCountsAsync(
            Range("00:00:00", "00:00:03"),
            boundary,
            CancellationToken.None);

        Assert.AreEqual(2, counts.ReceivedSampleCount);
    }

    [TestMethod]
    public async Task AppendAsync_WhenSealedSummaryCannotBePersisted_ThrowsStorageFailure()
    {
        var root = CreateTemporaryRoot();
        var layout = new ExecutionCaptureStorageLayout(root);
        var store = new ExecutionCaptureStore(layout, segmentDataLimitBytes: 3);
        try
        {
            await store.AppendAsync(Sample("00:00:01"), CancellationToken.None);
            File.WriteAllBytes(layout.GetSegmentSummaryPath(0), [0x00]);

            var failure = await Assert.ThrowsExactlyAsync<DiagnosticsException>(async () =>
                await store.AppendAsync(Sample("00:00:02"), CancellationToken.None));

            Assert.AreEqual(DiagnosticsErrorCode.ExecutionProfileStorageFailed, failure.ErrorCode);
        }
        finally
        {
            await store.DisposeAsync();
            DeleteDirectoryIfPresent(root);
        }
    }

    [TestMethod]
    public async Task AppendAsync_WhenSamplesHaveRegularCadence_UsesIncrementalTimestamps()
    {
        const int sampleCount = 10_000;
        const long expectedMaximumBytesPerSample = 5;
        await using var temporaryStore = CreateTemporaryStore();
        var store = temporaryStore.Store;
        var firstObservedAtUtc = Sample("00:00:01").ObservedAtUtc;
        for (var index = 0; index < sampleCount; index++)
        {
            await store.AppendAsync(
                new ExecutionSampleRecord(
                    firstObservedAtUtc.AddTicks(index * 2_000L),
                    ThreadId: 20_000,
                    StackId: 300),
                CancellationToken.None);
        }

        var boundary = store.CaptureReadBoundary();
        var dataLength = boundary.SegmentReadLimits.Sum(static segment => segment.DataLength);
        var records = await store.ReadAsync(
            new ExecutionTimeRange(firstObservedAtUtc, firstObservedAtUtc.AddSeconds(3)),
            boundary,
            CancellationToken.None).ToListAsync();

        Assert.IsLessThanOrEqualTo(sampleCount * expectedMaximumBytesPerSample, dataLength);
        Assert.HasCount(sampleCount, records);
        Assert.AreEqual(firstObservedAtUtc, records[0].ObservedAtUtc);
        Assert.AreEqual(firstObservedAtUtc.AddTicks((sampleCount - 1) * 2_000L), records[^1].ObservedAtUtc);
    }

    [TestMethod]
    public async Task ReadAsync_UsesCapturedBoundaryAndExcludesConcurrentAppend()
    {
        await using var temporaryStore = CreateTemporaryStore();
        var store = temporaryStore.Store;
        await store.AppendAsync(Sample("00:00:01"), CancellationToken.None);
        var boundary = store.CaptureReadBoundary();
        await store.AppendAsync(Sample("00:00:02"), CancellationToken.None);

        var records = await store.ReadAsync(Range("00:00:00", "00:00:03"), boundary, CancellationToken.None).ToListAsync();

        Assert.HasCount(1, records);
    }

    [TestMethod]
    public async Task ReadAsync_RemainsValidWhenAppendRotatesAnotherSegment()
    {
        await using var temporaryStore = CreateTemporaryStore(segmentDataLimitBytes: 3);
        var store = temporaryStore.Store;
        await store.AppendAsync(Sample("00:00:01"), CancellationToken.None);
        var boundary = store.CaptureReadBoundary();
        var reader = store.ReadAsync(Range("00:00:00", "00:00:03"), boundary, CancellationToken.None).GetAsyncEnumerator();
        try
        {
            Assert.IsTrue(await reader.MoveNextAsync());
            await store.AppendAsync(Sample("00:00:02"), CancellationToken.None);
            Assert.IsFalse(await reader.MoveNextAsync());
        }
        finally
        {
            await reader.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ReadAsync_TwoReadersOverlapPendingBoundaryPublishAndCompletePriorBoundary()
    {
        var writerPausedBeforeBoundaryPublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePendingAppend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ExecutionSampleRecord>[] firstRecordsRead =
        [
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously)
        ];
        var calls = 0;
        await using var temporaryStore = CreateTemporaryStore(beforeBoundaryPublishAsync: async () =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                writerPausedBeforeBoundaryPublish.TrySetResult();
                await releasePendingAppend.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
        var store = temporaryStore.Store;
        var committedSample = Sample("00:00:01");
        var pendingSample = Sample("00:00:02") with { ThreadId = 20_000 };
        ExecutionSampleRecord[] committedPrefix = [committedSample];
        await store.AppendAsync(committedSample, CancellationToken.None);
        var pendingAppend = store.AppendAsync(pendingSample, CancellationToken.None).AsTask();
        using var readerCancellation = new CancellationTokenSource(ConcurrentTestTimeout);
        Task<BoundaryObservation>[] readers = [];
        try
        {
            await writerPausedBeforeBoundaryPublish.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(pendingAppend.IsCompleted, "Append completed before readers captured the prior boundary.");
            readers =
            [
                ReadBoundaryWithFirstRecordHandshakeAsync(
                    store,
                    readerIndex: 0,
                    firstRecordsRead[0],
                    releasePendingAppend.Task,
                    readerCancellation.Token),
                ReadBoundaryWithFirstRecordHandshakeAsync(
                    store,
                    readerIndex: 1,
                    firstRecordsRead[1],
                    releasePendingAppend.Task,
                    readerCancellation.Token)
            ];

            var firstRecords = await Task.WhenAll(firstRecordsRead.Select(source => source.Task))
                .WaitAsync(TimeSpan.FromSeconds(5));
            ExecutionSampleRecord[] expectedFirstRecords = [committedSample, committedSample];

            AssertRecordPrefix(
                expectedFirstRecords,
                expectedFirstRecords.Length,
                firstRecords,
                "First-record handshakes");
            Assert.IsFalse(
                pendingAppend.IsCompleted,
                "Append completed before both readers proved that they were enumerating the prior boundary.");

            releasePendingAppend.TrySetResult();
            var observations = await Task.WhenAll(readers).WaitAsync(readerCancellation.Token);
            await pendingAppend.WaitAsync(readerCancellation.Token);

            Assert.HasCount(2, observations);
            foreach (var observation in observations)
            {
                Assert.AreEqual(
                    0,
                    observation.Boundary.LastCompletedRecord,
                    $"Reader {observation.ReaderIndex} captured a boundary containing the pending record.");
                AssertRecordPrefix(
                    committedPrefix,
                    committedPrefix.Length,
                    observation.Records,
                    $"Reader {observation.ReaderIndex} prior boundary");
            }

            var publishedBoundary = store.CaptureReadBoundary();
            var publishedRecords = await store.ReadAsync(
                Range("00:00:00", "00:00:03"),
                publishedBoundary,
                readerCancellation.Token).ToListAsync().AsTask().WaitAsync(readerCancellation.Token);
            ExecutionSampleRecord[] publishedSamples = [committedSample, pendingSample];

            Assert.AreEqual(
                observations[0].Boundary.LastCompletedRecord + 1,
                publishedBoundary.LastCompletedRecord,
                "Publishing the pending append must advance the stable boundary by exactly one record.");
            AssertRecordPrefix(publishedSamples, publishedSamples.Length, publishedRecords, "Published boundary");
        }
        finally
        {
            releasePendingAppend.TrySetResult();
            Task[] cleanupTasks = [pendingAppend, .. readers];
            var cleanup = Task.WhenAll(cleanupTasks);
            try
            {
                await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                readerCancellation.Cancel();
                await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                readerCancellation.Cancel();
            }
        }
    }

    [TestMethod]
    public async Task AppendAndReadAsync_UnderConcurrentBoundaryPressure_PreservesEveryCompletedRecord()
    {
        const int batchCount = 6;
        const int samplesPerBatch = 50;
        var expectedSamples = Enumerable.Range(0, batchCount * samplesPerBatch)
            .Select(index => new ExecutionSampleRecord(
                Sample("00:00:01").ObservedAtUtc.AddTicks(index),
                ThreadId: 1_000 + index,
                StackId: 2_000 + index))
            .ToArray();
        await using var temporaryStore = CreateTemporaryStore(segmentDataLimitBytes: 12);
        var store = temporaryStore.Store;
        using var cancellation = new CancellationTokenSource(ConcurrentTestTimeout);
        var schedule = new ConcurrentBoundarySchedule(readerCount: 2, observationCount: batchCount - 1);
        var stopwatch = Stopwatch.StartNew();
        var readerOne = ReadControlledBoundariesAsync(store, schedule, readerIndex: 0, cancellation);
        var readerTwo = ReadControlledBoundariesAsync(store, schedule, readerIndex: 1, cancellation);
        var writer = AppendInControlledBatchesAsync(store, expectedSamples, samplesPerBatch, schedule, cancellation);
        Task[] concurrentTasks = [readerOne, readerTwo, writer];
        try
        {
            await Task.WhenAll(concurrentTasks).WaitAsync(cancellation.Token);
            var observations = readerOne.GetAwaiter().GetResult()
                .Concat(readerTwo.GetAwaiter().GetResult())
                .OrderBy(observation => observation.ReaderIndex)
                .ThenBy(observation => observation.ObservationIndex)
                .ToArray();

            Assert.HasCount(2 * (batchCount - 1), observations);
            foreach (var observation in observations)
            {
                var expectedPrefixCount = (observation.ObservationIndex + 1) * samplesPerBatch;
                Assert.AreEqual(
                    expectedPrefixCount - 1,
                    observation.Boundary.LastCompletedRecord,
                    $"Reader {observation.ReaderIndex}, observation {observation.ObservationIndex} captured an unexpected boundary.");
                AssertRecordPrefix(
                    expectedSamples,
                    expectedPrefixCount,
                    observation.Records,
                    $"Reader {observation.ReaderIndex}, observation {observation.ObservationIndex}");
            }

            var finalBoundary = store.CaptureReadBoundary();
            var records = await store.ReadAsync(
                Range("00:00:00", "00:01:00"),
                finalBoundary,
                cancellation.Token).ToListAsync().AsTask().WaitAsync(cancellation.Token);
            stopwatch.Stop();

            Assert.AreEqual(expectedSamples.Length - 1, finalBoundary.LastCompletedRecord);
            AssertRecordPrefix(expectedSamples, expectedSamples.Length, records, "Final boundary");
            Assert.IsTrue(stopwatch.Elapsed < ConcurrentTestTimeout, $"Elapsed: {stopwatch.Elapsed}.");
        }
        finally
        {
            cancellation.Cancel();
            schedule.ReleaseAll();
            await Task.WhenAll(concurrentTasks).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task AppendAsync_WhenCommitFailsAfterWrite_PoisonsLaterAppendAndKeepsPriorBoundaryReadable()
    {
        var calls = 0;
        await using var temporaryStore = CreateTemporaryStore(beforeBoundaryPublishAsync: () =>
            Interlocked.Increment(ref calls) == 2
                ? Task.FromException(new IOException("commit fault"))
                : Task.CompletedTask);
        var store = temporaryStore.Store;
        await store.AppendAsync(Sample("00:00:01"), CancellationToken.None);
        var boundary = store.CaptureReadBoundary();

        var firstFailure = await Assert.ThrowsExactlyAsync<DiagnosticsException>(async () =>
            await store.AppendAsync(Sample("00:00:02"), CancellationToken.None));
        var repeatedFailure = await Assert.ThrowsExactlyAsync<DiagnosticsException>(async () =>
            await store.AppendAsync(Sample("00:00:03"), CancellationToken.None));
        var records = await store.ReadAsync(Range("00:00:00", "00:00:04"), boundary, CancellationToken.None).ToListAsync();

        Assert.AreEqual(DiagnosticsErrorCode.ExecutionProfileStorageFailed, firstFailure.ErrorCode);
        Assert.IsInstanceOfType<IOException>(firstFailure.InnerException);
        Assert.AreSame(firstFailure, repeatedFailure);
        Assert.HasCount(1, records);
    }

    [TestMethod]
    public async Task CaptureReadBoundary_WhenFlushFails_PoisonsLaterAppend()
    {
        var root = CreateTemporaryRoot();
        var failNextFlush = 0;
        var store = new ExecutionCaptureStore(
            new ExecutionCaptureStorageLayout(root),
            beforeSegmentFlush: () =>
            {
                if (Interlocked.Exchange(ref failNextFlush, 0) != 0)
                {
                    throw new IOException("controlled flush failure");
                }
            });
        try
        {
            var committedSample = Sample("00:00:01");
            await store.AppendAsync(committedSample, CancellationToken.None);
            var readableBoundary = store.CaptureReadBoundary();
            Volatile.Write(ref failNextFlush, 1);

            var firstFailure = Assert.ThrowsExactly<DiagnosticsException>(store.CaptureReadBoundary);
            var repeatedBoundaryFailure = Assert.ThrowsExactly<DiagnosticsException>(store.CaptureReadBoundary);
            var repeatedFailure = await Assert.ThrowsExactlyAsync<DiagnosticsException>(async () =>
                await store.AppendAsync(Sample("00:00:02"), CancellationToken.None));
            var readableRecords = await store.ReadAsync(
                Range("00:00:00", "00:00:02"),
                readableBoundary,
                CancellationToken.None).ToListAsync();

            Assert.AreEqual(DiagnosticsErrorCode.ExecutionProfileStorageFailed, firstFailure.ErrorCode);
            Assert.IsInstanceOfType<IOException>(firstFailure.InnerException);
            Assert.AreSame(firstFailure, repeatedBoundaryFailure);
            Assert.AreSame(firstFailure, repeatedFailure);
            CollectionAssert.AreEqual(new[] { committedSample }, readableRecords.ToArray());
        }
        finally
        {
            await store.DisposeAsync();
            DeleteDirectoryIfPresent(root);
        }
    }

    [TestMethod]
    public async Task ReadAsync_CancellingOneReadDoesNotCancelAnotherRead()
    {
        await using var temporaryStore = CreateTemporaryStore();
        var store = temporaryStore.Store;
        await store.AppendAsync(Sample("00:00:01"), CancellationToken.None);
        await store.AppendAsync(Sample("00:00:02"), CancellationToken.None);
        var boundary = store.CaptureReadBoundary();
        using var cancellation = new CancellationTokenSource();
        var cancelledReader = store.ReadAsync(Range("00:00:00", "00:00:03"), boundary, cancellation.Token)
            .GetAsyncEnumerator();
        var unaffectedReader = store.ReadAsync(Range("00:00:00", "00:00:03"), boundary, CancellationToken.None)
            .GetAsyncEnumerator();
        try
        {
            Assert.IsTrue(await cancelledReader.MoveNextAsync());
            Assert.IsTrue(await unaffectedReader.MoveNextAsync());
            cancellation.Cancel();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await cancelledReader.MoveNextAsync());
            Assert.IsTrue(await unaffectedReader.MoveNextAsync());
            Assert.IsFalse(await unaffectedReader.MoveNextAsync());
        }
        finally
        {
            await cancelledReader.DisposeAsync();
            await unaffectedReader.DisposeAsync();
        }

    }

    [TestMethod]
    public async Task DisposeAsync_WaitsForRegisteredReaderAndDeletesSessionDirectory()
    {
        var root = CreateTemporaryRoot();
        var layout = new ExecutionCaptureStorageLayout(root);
        var store = new ExecutionCaptureStore(layout);
        try
        {
            await store.AppendAsync(Sample("00:00:01"), CancellationToken.None);
            var boundary = store.CaptureReadBoundary();
            var reader = store.ReadAsync(Range("00:00:00", "00:00:02"), boundary, CancellationToken.None)
                .GetAsyncEnumerator();
            Assert.IsTrue(await reader.MoveNextAsync());

            var disposeTask = store.DisposeAsync();

            Assert.IsFalse(disposeTask.IsCompleted);
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
                await store.ReadAsync(Range("00:00:00", "00:00:02"), boundary, CancellationToken.None).ToListAsync());
            await reader.DisposeAsync();
            await disposeTask;
            Assert.IsFalse(Directory.Exists(layout.SessionDirectory));
        }
        finally
        {
            await store.DisposeAsync();
            DeleteDirectoryIfPresent(root);
        }
    }

    [TestMethod]
    public void ExecutionCaptureStorageLayout_WhenSessionDirectoryCannotBeCreated_ThrowsStorageFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        File.WriteAllText(root, "not-a-directory");
        try
        {
            var exception = Assert.ThrowsExactly<DiagnosticsException>(() => new ExecutionCaptureStorageLayout(root));

            Assert.AreEqual(DiagnosticsErrorCode.ExecutionProfileStorageFailed, exception.ErrorCode);
            Assert.IsInstanceOfType<IOException>(exception.InnerException);
        }
        finally
        {
            File.Delete(root);
        }
    }

    [TestMethod]
    public async Task ReadAsync_WhenSegmentIsCorrupt_ThrowsStorageFailure()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var layout = new ExecutionCaptureStorageLayout(root);
            await using (var store = new ExecutionCaptureStore(layout))
            {
                await store.AppendAsync(Sample("00:00:01"), CancellationToken.None);
                var boundary = store.CaptureReadBoundary();
                await using (var corruptingStream = new FileStream(
                    layout.GetSegmentPath(0),
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 1,
                    options: FileOptions.Asynchronous))
                {
                    await corruptingStream.WriteAsync(new byte[] { 0 });
                }

                var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(async () =>
                    await store.ReadAsync(Range("00:00:00", "00:00:02"), boundary, CancellationToken.None).ToListAsync());

                Assert.AreEqual(DiagnosticsErrorCode.ExecutionProfileStorageFailed, exception.ErrorCode);
                Assert.IsInstanceOfType<IOException>(exception.InnerException);
            }
        }
        finally
        {
            DeleteDirectoryIfPresent(root);
        }
    }

    private static TemporaryExecutionCaptureStore CreateTemporaryStore(int? segmentDataLimitBytes = null, Func<Task>? beforeBoundaryPublishAsync = null)
    {
        var root = CreateTemporaryRoot();
        var layout = new ExecutionCaptureStorageLayout(root);
        var store = segmentDataLimitBytes is null
            ? new ExecutionCaptureStore(layout)
            : new ExecutionCaptureStore(layout, segmentDataLimitBytes.Value, beforeBoundaryPublishAsync);
        if (segmentDataLimitBytes is null)
        {
            store = new ExecutionCaptureStore(layout, beforeBoundaryPublishAsync: beforeBoundaryPublishAsync);
        }
        return new TemporaryExecutionCaptureStore(root, store);
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static int GetSealedSegmentSummaryCollectionCount(
        ExecutionCaptureStore store,
        string collectionPropertyName)
    {
        var segmentsField = typeof(ExecutionCaptureStore).GetField(
            "_segments",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Execution capture store segment collection is unavailable.");
        var segments = (System.Collections.IList)(segmentsField.GetValue(store)
            ?? throw new InvalidOperationException("Execution capture store segments are unavailable."));
        var sealedSegment = segments.Cast<object>().Single(segment =>
            (bool)(segment.GetType().GetProperty("IsSealed")?.GetValue(segment)
                ?? throw new InvalidOperationException("Execution capture segment seal state is unavailable.")));
        var collection = sealedSegment.GetType().GetProperty(collectionPropertyName)?.GetValue(sealedSegment)
            ?? throw new InvalidOperationException($"Execution capture segment {collectionPropertyName} is unavailable.");
        return (int)(collection.GetType().GetProperty("Count")?.GetValue(collection)
            ?? throw new InvalidOperationException($"Execution capture segment {collectionPropertyName} count is unavailable."));
    }

    private static ExecutionSampleRecord Sample(string time) => new(
        DateTimeOffset.ParseExact(
            $"2026-09-07T{time}Z",
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal),
        ThreadId: 7,
        StackId: 3);

    private static ExecutionTimeRange Range(string start, string end) => new(
        Sample(start).ObservedAtUtc,
        Sample(end).ObservedAtUtc);

    private static void DeleteDirectoryIfPresent(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static async Task<IReadOnlyList<BoundaryObservation>> ReadControlledBoundariesAsync(
        ExecutionCaptureStore store,
        ConcurrentBoundarySchedule schedule,
        int readerIndex,
        CancellationTokenSource cancellation)
    {
        var observations = new List<BoundaryObservation>(schedule.ObservationCount);
        schedule.SignalReaderStarted(readerIndex);
        try
        {
            for (var observationIndex = 0; observationIndex < schedule.ObservationCount; observationIndex++)
            {
                await schedule.WaitForCaptureRequestAsync(observationIndex, cancellation.Token);
                var boundary = store.CaptureReadBoundary();
                schedule.SignalBoundaryCaptured(readerIndex, observationIndex);
                await schedule.WaitForAppendStartedAsync(observationIndex, cancellation.Token);
                var records = await store.ReadAsync(
                    Range("00:00:00", "00:01:00"),
                    boundary,
                    cancellation.Token).ToListAsync().AsTask().WaitAsync(cancellation.Token);
                observations.Add(new BoundaryObservation(readerIndex, observationIndex, boundary, records));
                schedule.SignalReadCompleted(readerIndex, observationIndex);
            }

            return observations;
        }
        catch
        {
            cancellation.Cancel();
            throw;
        }
    }

    private static async Task<BoundaryObservation> ReadBoundaryWithFirstRecordHandshakeAsync(
        ExecutionCaptureStore store,
        int readerIndex,
        TaskCompletionSource<ExecutionSampleRecord> firstRecordRead,
        Task releaseAfterFirstRecordsRead,
        CancellationToken cancellationToken)
    {
        var boundary = store.CaptureReadBoundary();
        var records = new List<ExecutionSampleRecord>();
        await using var reader = store.ReadAsync(
            Range("00:00:00", "00:00:03"),
            boundary,
            cancellationToken).GetAsyncEnumerator(cancellationToken);
        var hasFirstRecord = await reader.MoveNextAsync().AsTask().WaitAsync(cancellationToken);
        Assert.IsTrue(hasFirstRecord, $"Reader {readerIndex} did not observe the committed prefix.");
        records.Add(reader.Current);
        firstRecordRead.TrySetResult(reader.Current);
        await releaseAfterFirstRecordsRead.WaitAsync(cancellationToken);

        while (await reader.MoveNextAsync().AsTask().WaitAsync(cancellationToken))
        {
            records.Add(reader.Current);
        }

        return new BoundaryObservation(readerIndex, ObservationIndex: 0, boundary, records);
    }

    private static async Task AppendInControlledBatchesAsync(
        ExecutionCaptureStore store,
        IReadOnlyList<ExecutionSampleRecord> samples,
        int samplesPerBatch,
        ConcurrentBoundarySchedule schedule,
        CancellationTokenSource cancellation)
    {
        try
        {
            await schedule.WaitForAllReadersStartedAsync(cancellation.Token);
            await AppendBatchAsync(store, samples, startIndex: 0, samplesPerBatch, cancellation.Token);
            for (var observationIndex = 0; observationIndex < schedule.ObservationCount; observationIndex++)
            {
                schedule.RequestBoundaryCapture(observationIndex);
                await schedule.WaitForAllBoundariesCapturedAsync(observationIndex, cancellation.Token);
                var batchStartIndex = (observationIndex + 1) * samplesPerBatch;
                await AppendBatchAsync(
                    store,
                    samples,
                    batchStartIndex,
                    count: 1,
                    cancellation.Token);
                schedule.SignalAppendStarted(observationIndex);
                await AppendBatchAsync(
                    store,
                    samples,
                    startIndex: batchStartIndex + 1,
                    count: samplesPerBatch - 1,
                    cancellation.Token);
                await schedule.WaitForAllReadsCompletedAsync(observationIndex, cancellation.Token);
            }
        }
        catch
        {
            cancellation.Cancel();
            throw;
        }
    }

    private static async Task AppendBatchAsync(
        ExecutionCaptureStore store,
        IReadOnlyList<ExecutionSampleRecord> samples,
        int startIndex,
        int count,
        CancellationToken cancellationToken)
    {
        for (var index = startIndex; index < startIndex + count; index++)
        {
            await store.AppendAsync(samples[index], cancellationToken).AsTask().WaitAsync(cancellationToken);
        }
    }

    private static void AssertRecordPrefix(
        ExecutionSampleRecord[] expected,
        int expectedCount,
        IReadOnlyList<ExecutionSampleRecord> actual,
        string context)
    {
        Assert.HasCount(expectedCount, actual, $"{context} returned an unexpected record count.");
        for (var index = 0; index < expectedCount; index++)
        {
            Assert.AreEqual(
                expected[index].ObservedAtUtc,
                actual[index].ObservedAtUtc,
                $"{context}, record {index} changed ObservedAtUtc.");
            Assert.AreEqual(
                expected[index].ThreadId,
                actual[index].ThreadId,
                $"{context}, record {index} changed ThreadId.");
            Assert.AreEqual(
                expected[index].StackId,
                actual[index].StackId,
                $"{context}, record {index} changed StackId.");
        }
    }

    private sealed record BoundaryObservation(
        int ReaderIndex,
        int ObservationIndex,
        ExecutionCaptureReadBoundary Boundary,
        IReadOnlyList<ExecutionSampleRecord> Records);

    private sealed class ConcurrentBoundarySchedule
    {
        private readonly TaskCompletionSource[] _readersStarted;
        private readonly TaskCompletionSource[] _captureRequests;
        private readonly TaskCompletionSource[][] _boundariesCaptured;
        private readonly TaskCompletionSource[] _appendStarted;
        private readonly TaskCompletionSource[][] _readsCompleted;

        public ConcurrentBoundarySchedule(int readerCount, int observationCount)
        {
            _readersStarted = CreateCompletionSources(readerCount);
            _captureRequests = CreateCompletionSources(observationCount);
            _boundariesCaptured = CreateCompletionSourceMatrix(observationCount, readerCount);
            _appendStarted = CreateCompletionSources(observationCount);
            _readsCompleted = CreateCompletionSourceMatrix(observationCount, readerCount);
        }

        public int ObservationCount => _captureRequests.Length;

        public void SignalReaderStarted(int readerIndex) => _readersStarted[readerIndex].TrySetResult();

        public Task WaitForAllReadersStartedAsync(CancellationToken cancellationToken) =>
            Task.WhenAll(_readersStarted.Select(source => source.Task)).WaitAsync(cancellationToken);

        public void RequestBoundaryCapture(int observationIndex) => _captureRequests[observationIndex].TrySetResult();

        public Task WaitForCaptureRequestAsync(int observationIndex, CancellationToken cancellationToken) =>
            _captureRequests[observationIndex].Task.WaitAsync(cancellationToken);

        public void SignalBoundaryCaptured(int readerIndex, int observationIndex) =>
            _boundariesCaptured[observationIndex][readerIndex].TrySetResult();

        public Task WaitForAllBoundariesCapturedAsync(int observationIndex, CancellationToken cancellationToken) =>
            Task.WhenAll(_boundariesCaptured[observationIndex].Select(source => source.Task)).WaitAsync(cancellationToken);

        public void SignalAppendStarted(int observationIndex) => _appendStarted[observationIndex].TrySetResult();

        public Task WaitForAppendStartedAsync(int observationIndex, CancellationToken cancellationToken) =>
            _appendStarted[observationIndex].Task.WaitAsync(cancellationToken);

        public void SignalReadCompleted(int readerIndex, int observationIndex) =>
            _readsCompleted[observationIndex][readerIndex].TrySetResult();

        public Task WaitForAllReadsCompletedAsync(int observationIndex, CancellationToken cancellationToken) =>
            Task.WhenAll(_readsCompleted[observationIndex].Select(source => source.Task)).WaitAsync(cancellationToken);

        public void ReleaseAll()
        {
            foreach (var source in _readersStarted
                .Concat(_captureRequests)
                .Concat(_boundariesCaptured.SelectMany(sources => sources))
                .Concat(_appendStarted)
                .Concat(_readsCompleted.SelectMany(sources => sources)))
            {
                source.TrySetResult();
            }
        }

        private static TaskCompletionSource[] CreateCompletionSources(int count) =>
            Enumerable.Range(0, count)
                .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .ToArray();

        private static TaskCompletionSource[][] CreateCompletionSourceMatrix(int rowCount, int columnCount) =>
            Enumerable.Range(0, rowCount)
                .Select(_ => CreateCompletionSources(columnCount))
                .ToArray();
    }

    private sealed class TemporaryExecutionCaptureStore : IAsyncDisposable
    {
        private readonly string _root;

        public TemporaryExecutionCaptureStore(string root, ExecutionCaptureStore store)
        {
            _root = root;
            Store = store;
        }

        public ExecutionCaptureStore Store { get; }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            DeleteDirectoryIfPresent(_root);
        }
    }
}
