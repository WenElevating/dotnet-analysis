using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace DotnetAnalysis.Tests.Diagnostics;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class ExecutionCaptureStoreTests
{
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

    private static TemporaryExecutionCaptureStore CreateTemporaryStore(int? segmentDataLimitBytes = null)
    {
        var root = CreateTemporaryRoot();
        var layout = new ExecutionCaptureStorageLayout(root);
        var store = segmentDataLimitBytes is null
            ? new ExecutionCaptureStore(layout)
            : new ExecutionCaptureStore(layout, segmentDataLimitBytes.Value);
        return new TemporaryExecutionCaptureStore(root, store);
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
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
