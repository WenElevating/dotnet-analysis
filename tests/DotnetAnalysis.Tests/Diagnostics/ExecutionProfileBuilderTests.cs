using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace DotnetAnalysis.Tests.Diagnostics;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class ExecutionProfileBuilderTests
{
    private static readonly string[] ExpectedHotspots =
    [
        "Root|App|4|0",
        "SharedLeaf|App|3|3",
        "CallerOne|App|2|0",
        "Alpha|ModuleA|1|1",
        "Alpha|ModuleB|1|1",
        "OtherLeaf|App|1|1",
        "Zeta|ModuleC|1|1",
        "CallerTwo|App|1|0"
    ];

    private static readonly string[] ExpectedRootToLeafFrameNames = ["Root", "Leaf"];

    [TestMethod]
    public async Task BuildAsync_AggregatesPathsHotspotsAndLostEventsWithStableOrdering()
    {
        await using var temporaryStore = CreateTemporaryStore();
        var store = temporaryStore.Store;
        var root = await AddFrameAsync(store, "Root", "App", "root");
        var callerOne = await AddFrameAsync(store, "CallerOne", "App", "caller-one");
        var callerTwo = await AddFrameAsync(store, "CallerTwo", "App", "caller-two");
        var sharedLeaf = await AddFrameAsync(store, "SharedLeaf", "App", "shared-leaf");
        var otherLeaf = await AddFrameAsync(store, "OtherLeaf", "App", "other-leaf");
        var alphaModuleB = await AddFrameAsync(store, "Alpha", "ModuleB", "alpha-b");
        var alphaModuleA = await AddFrameAsync(store, "Alpha", "ModuleA", "alpha-a");
        var zeta = await AddFrameAsync(store, "Zeta", "ModuleC", "zeta");

        var rootStack = await store.GetOrAddStackAsync(-1, root, CancellationToken.None);
        var callerOneStack = await store.GetOrAddStackAsync(rootStack, callerOne, CancellationToken.None);
        var callerOneLeafStack = await store.GetOrAddStackAsync(callerOneStack, sharedLeaf, CancellationToken.None);
        var callerTwoStack = await store.GetOrAddStackAsync(rootStack, callerTwo, CancellationToken.None);
        var callerTwoLeafStack = await store.GetOrAddStackAsync(callerTwoStack, sharedLeaf, CancellationToken.None);
        var otherLeafStack = await store.GetOrAddStackAsync(rootStack, otherLeaf, CancellationToken.None);
        var alphaModuleBStack = await store.GetOrAddStackAsync(-1, alphaModuleB, CancellationToken.None);
        var alphaModuleAStack = await store.GetOrAddStackAsync(-1, alphaModuleA, CancellationToken.None);
        var zetaStack = await store.GetOrAddStackAsync(-1, zeta, CancellationToken.None);
        await AppendAsync(store, "00:00:01", callerOneLeafStack);
        await AppendAsync(store, "00:00:02", callerOneLeafStack);
        await AppendAsync(store, "00:00:03", callerTwoLeafStack);
        await AppendAsync(store, "00:00:04", otherLeafStack);
        await AppendAsync(store, "00:00:05", alphaModuleBStack);
        await AppendAsync(store, "00:00:06", alphaModuleAStack);
        await AppendAsync(store, "00:00:07", zetaStack);
        var boundary = store.CaptureReadBoundary();

        var profile = await new ExecutionProfileBuilder(store).BuildAsync(
            Range("00:00:00", "00:00:08"),
            boundary,
            lostEventCount: 13,
            CancellationToken.None);

        Assert.AreEqual(7, profile.ReceivedSampleCount);
        Assert.AreEqual(13, profile.LostEventCount);
        CollectionAssert.AreEqual(
            ExpectedHotspots,
            profile.Hotspots
                .Select(hotspot => $"{hotspot.Frame.MethodName}|{hotspot.Frame.ModuleName}|{hotspot.InclusiveSampleCount}|{hotspot.ExclusiveSampleCount}")
                .ToArray());

        var rootNode = profile.CallTreeRoots.Single(node => node.Frame.MethodName == "Root");
        Assert.AreEqual(4, rootNode.InclusiveSampleCount);
        Assert.AreEqual(0, rootNode.ExclusiveSampleCount);
        var callerOneNode = rootNode.Children.Single(node => node.Frame.MethodName == "CallerOne");
        var callerTwoNode = rootNode.Children.Single(node => node.Frame.MethodName == "CallerTwo");
        var otherLeafNode = rootNode.Children.Single(node => node.Frame.MethodName == "OtherLeaf");
        var callerOneLeafNode = AssertSingleChild(callerOneNode);
        var callerTwoLeafNode = AssertSingleChild(callerTwoNode);

        Assert.AreEqual(2, callerOneNode.InclusiveSampleCount);
        Assert.AreEqual(0, callerOneNode.ExclusiveSampleCount);
        Assert.AreEqual(2, callerOneLeafNode.InclusiveSampleCount);
        Assert.AreEqual(2, callerOneLeafNode.ExclusiveSampleCount);
        Assert.AreEqual(1, callerTwoNode.InclusiveSampleCount);
        Assert.AreEqual(0, callerTwoNode.ExclusiveSampleCount);
        Assert.AreEqual(1, callerTwoLeafNode.InclusiveSampleCount);
        Assert.AreEqual(1, callerTwoLeafNode.ExclusiveSampleCount);
        Assert.AreEqual(1, otherLeafNode.InclusiveSampleCount);
        Assert.AreEqual(1, otherLeafNode.ExclusiveSampleCount);
        Assert.AreNotSame(callerOneLeafNode, callerTwoLeafNode);
        Assert.AreEqual(callerOneLeafNode.Frame, callerTwoLeafNode.Frame);
    }

    [TestMethod]
    public async Task BuildAsync_WhenRangeContainsNoSamples_ReturnsEmptyProfile()
    {
        await using var temporaryStore = CreateTemporaryStore();
        var store = temporaryStore.Store;
        var frameId = await AddFrameAsync(store, "Worker", "App", "worker");
        var stackId = await store.GetOrAddStackAsync(-1, frameId, CancellationToken.None);
        await AppendAsync(store, "00:00:01", stackId);
        var range = Range("00:00:02", "00:00:03");

        var profile = await new ExecutionProfileBuilder(store).BuildAsync(
            range,
            store.CaptureReadBoundary(),
            lostEventCount: 0,
            CancellationToken.None);

        Assert.AreEqual(range, profile.TimeRange);
        Assert.AreEqual(0, profile.ReceivedSampleCount);
        Assert.AreEqual(0, profile.LostEventCount);
        Assert.IsEmpty(profile.Hotspots);
        Assert.IsEmpty(profile.CallTreeRoots);
    }

    [TestMethod]
    public async Task BuildAsync_UsesSuppliedReadBoundaryAndExcludesLaterSamples()
    {
        await using var temporaryStore = CreateTemporaryStore();
        var store = temporaryStore.Store;
        var frameId = await AddFrameAsync(store, "Worker", "App", "worker");
        var stackId = await store.GetOrAddStackAsync(-1, frameId, CancellationToken.None);
        await AppendAsync(store, "00:00:01", stackId);
        var boundary = store.CaptureReadBoundary();
        await AppendAsync(store, "00:00:02", stackId);

        var profile = await new ExecutionProfileBuilder(store).BuildAsync(
            Range("00:00:00", "00:00:03"),
            boundary,
            lostEventCount: 0,
            CancellationToken.None);

        Assert.AreEqual(1, profile.ReceivedSampleCount);
        Assert.AreEqual(1, profile.Hotspots.Single().InclusiveSampleCount);
    }

    [TestMethod]
    public async Task BuildAsync_WhenCancelled_SubsequentQueryRemainsUsable()
    {
        await using var temporaryStore = CreateTemporaryStore();
        var store = temporaryStore.Store;
        var frameId = await AddFrameAsync(store, "Worker", "App", "worker");
        var stackId = await store.GetOrAddStackAsync(-1, frameId, CancellationToken.None);
        await AppendAsync(store, "00:00:01", stackId);
        var range = Range("00:00:00", "00:00:02");
        var boundary = store.CaptureReadBoundary();
        var builder = new ExecutionProfileBuilder(store);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await builder.BuildAsync(range, boundary, lostEventCount: 0, cancellation.Token));

        var profile = await builder.BuildAsync(range, boundary, lostEventCount: 0, CancellationToken.None);

        Assert.AreEqual(1, profile.ReceivedSampleCount);
        Assert.AreEqual("Worker", profile.Hotspots.Single().Frame.MethodName);
    }

    [TestMethod]
    public async Task BuildAsync_WhenCancelledDuringPostReadHotspotSort_SubsequentQueryRemainsUsable()
    {
        await using var temporaryStore = CreateTemporaryStore();
        var store = temporaryStore.Store;
        const int sampleCount = 128;
        for (var index = 0; index < sampleCount; index++)
        {
            var frameId = await AddFrameAsync(
                store,
                $"Worker{sampleCount - index:D3}",
                "App",
                $"worker-{index}");
            var stackId = await store.GetOrAddStackAsync(-1, frameId, CancellationToken.None);
            await AppendAsync(store, "00:00:01", stackId);
        }

        var range = Range("00:00:00", "00:00:02");
        var boundary = store.CaptureReadBoundary();
        using var cancellation = new CancellationTokenSource();
        var hotspotComparisons = 0;
        var cancelledBuilder = new ExecutionProfileBuilder(
            store,
            beforeHotspotComparison: () =>
            {
                if (Interlocked.Increment(ref hotspotComparisons) == 1)
                {
                    cancellation.Cancel();
                }
            });

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await cancelledBuilder.BuildAsync(range, boundary, lostEventCount: 0, cancellation.Token));

        Assert.IsGreaterThan(0, hotspotComparisons);
        var profile = await new ExecutionProfileBuilder(store).BuildAsync(
            range,
            boundary,
            lostEventCount: 0,
            CancellationToken.None);

        Assert.AreEqual(sampleCount, profile.ReceivedSampleCount);
        Assert.HasCount(sampleCount, profile.Hotspots);
    }

    [TestMethod]
    public async Task BuildAsync_WhenCancelledDuringRecursiveCallTreeMaterialization_SubsequentQueryRemainsUsable()
    {
        await using var temporaryStore = CreateTemporaryStore();
        var store = temporaryStore.Store;
        var rootFrameId = await AddFrameAsync(store, "Root", "App", "root");
        var leafFrameId = await AddFrameAsync(store, "Leaf", "App", "leaf");
        var rootStackId = await store.GetOrAddStackAsync(-1, rootFrameId, CancellationToken.None);
        var leafStackId = await store.GetOrAddStackAsync(rootStackId, leafFrameId, CancellationToken.None);
        await AppendAsync(store, "00:00:01", leafStackId);
        var range = Range("00:00:00", "00:00:02");
        var boundary = store.CaptureReadBoundary();
        using var cancellation = new CancellationTokenSource();
        var materializedNodeCount = 0;
        var cancelledBuilder = new ExecutionProfileBuilder(
            store,
            beforeCallTreeNodeMaterialization: () =>
            {
                if (Interlocked.Increment(ref materializedNodeCount) == 2)
                {
                    cancellation.Cancel();
                }
            });

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await cancelledBuilder.BuildAsync(range, boundary, lostEventCount: 0, cancellation.Token));

        Assert.AreEqual(2, materializedNodeCount);
        var profile = await new ExecutionProfileBuilder(store).BuildAsync(
            range,
            boundary,
            lostEventCount: 0,
            CancellationToken.None);

        Assert.AreEqual(1, profile.ReceivedSampleCount);
        Assert.AreEqual("Root", profile.CallTreeRoots.Single().Frame.MethodName);
    }

    [TestMethod]
    public async Task BuildAsync_WhenDisposeOwnsWriterDuringStackResolution_CompletesAndCleansUp()
    {
        var stackResolutionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStackResolution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposerOwnsWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var temporaryStore = CreateTemporaryStore(
            beforeStackFramesReadAsync: async () =>
            {
                stackResolutionEntered.TrySetResult();
                await releaseStackResolution.Task.WaitAsync(TimeSpan.FromSeconds(5));
            },
            afterDisposeWriterAcquiredAsync: () =>
            {
                disposerOwnsWriter.TrySetResult();
                return Task.CompletedTask;
            });
        var store = temporaryStore.Store;
        var frameId = await AddFrameAsync(store, "Worker", "App", "worker");
        var stackId = await store.GetOrAddStackAsync(-1, frameId, CancellationToken.None);
        await AppendAsync(store, "00:00:01", stackId);
        var builder = new ExecutionProfileBuilder(store);
        using var cancellation = new CancellationTokenSource();
        Task<ExecutionProfile>? buildTask = null;
        Task? disposeTask = null;
        try
        {
            buildTask = builder.BuildAsync(
                Range("00:00:00", "00:00:02"),
                store.CaptureReadBoundary(),
                lostEventCount: 0,
                cancellation.Token);
            await stackResolutionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            disposeTask = store.DisposeAsync().AsTask();
            await disposerOwnsWriter.Task.WaitAsync(TimeSpan.FromSeconds(5));
            releaseStackResolution.TrySetResult();

            var profile = await buildTask.WaitAsync(TimeSpan.FromSeconds(5));
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(1, profile.ReceivedSampleCount);
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(temporaryStore.Root));
        }
        finally
        {
            cancellation.Cancel();
            releaseStackResolution.TrySetResult();
            if (buildTask is not null)
            {
                await IgnoreExpectedCancellationAsync(buildTask);
            }

            if (disposeTask is not null)
            {
                await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [TestMethod]
    public async Task BuildAsync_ResolvesSourceByStableFrameIdentity()
    {
        await using var temporaryStore = CreateTemporaryStore();
        var store = temporaryStore.Store;
        var firstFrameId = await AddFrameAsync(store, "Worker.Run", "Worker", "first-address");
        var secondFrameId = await AddFrameAsync(store, "Worker.Run", "Worker", "second-address");
        var firstStackId = await store.GetOrAddStackAsync(-1, firstFrameId, CancellationToken.None);
        var secondStackId = await store.GetOrAddStackAsync(-1, secondFrameId, CancellationToken.None);
        await AppendAsync(store, "00:00:01", firstStackId);
        await AppendAsync(store, "00:00:02", secondStackId);
        await AppendAsync(store, "00:00:03", secondStackId);
        var boundary = store.CaptureReadBoundary();
        var resolvedFrameIds = new List<int>();
        var builder = new ExecutionProfileBuilder(
            store,
            sourceLocationResolver: (frame, _) =>
            {
                resolvedFrameIds.Add(frame.FrameId);
                var lineNumber = frame.FrameId == firstFrameId ? 101 : 202;
                return Task.FromResult<SourceLocation?>(new SourceLocation(
                    $"C:\\source-{frame.FrameId}.cs",
                    lineNumber,
                    columnNumber: null));
            });

        var profile = await builder.BuildAsync(
            Range("00:00:00", "00:00:03"),
            boundary,
            lostEventCount: 0,
            CancellationToken.None);

        Assert.HasCount(2, profile.Hotspots);
        CollectionAssert.AreEqual(new[] { firstFrameId, secondFrameId }, resolvedFrameIds);
        Assert.AreEqual(101, profile.Hotspots[0].Frame.SourceLocation?.LineNumber);
        Assert.AreEqual(202, profile.Hotspots[1].Frame.SourceLocation?.LineNumber);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task BuildAsync_RepeatedFrameCacheHits_DoNotAllocatePerObservedFrame()
    {
        await using var temporaryStore = CreateTemporaryStore();
        var store = temporaryStore.Store;
        const int sampleCount = 4_096;
        const int deepStackDepth = 24;
        var shallowFrameId = await AddFrameAsync(store, "Shallow", "App", "shallow");
        var shallowStackId = await store.GetOrAddStackAsync(-1, shallowFrameId, CancellationToken.None);
        var deepStackId = -1;
        for (var index = 0; index < deepStackDepth; index++)
        {
            var frameId = await AddFrameAsync(store, $"Deep{index:D2}", "App", $"deep-{index}");
            deepStackId = await store.GetOrAddStackAsync(deepStackId, frameId, CancellationToken.None);
        }

        var shallowObservedAtUtc = Instant("00:00:01");
        var deepObservedAtUtc = Instant("00:00:03");
        for (var index = 0; index < sampleCount; index++)
        {
            await store.AppendAsync(
                new ExecutionSampleRecord(shallowObservedAtUtc, ThreadId: 7, shallowStackId),
                CancellationToken.None);
            await store.AppendAsync(
                new ExecutionSampleRecord(deepObservedAtUtc, ThreadId: 7, deepStackId),
                CancellationToken.None);
        }

        var boundary = store.CaptureReadBoundary();
        var shallowRange = Range("00:00:00", "00:00:02");
        var deepRange = Range("00:00:02", "00:00:04");
        var builder = new ExecutionProfileBuilder(store);
        _ = await builder.BuildAsync(shallowRange, boundary, lostEventCount: 0, CancellationToken.None);
        _ = await builder.BuildAsync(deepRange, boundary, lostEventCount: 0, CancellationToken.None);

        var shallowAllocatedBytes = await MeasureBuildAllocatedBytesAsync(builder, shallowRange, boundary);
        var deepAllocatedBytes = await MeasureBuildAllocatedBytesAsync(builder, deepRange, boundary);
        var additionalAllocatedBytes = deepAllocatedBytes - shallowAllocatedBytes;
        Console.WriteLine(
            $"shallow={shallowAllocatedBytes}; deep={deepAllocatedBytes}; additional={additionalAllocatedBytes}");

        Assert.IsLessThan(1_000_000L, additionalAllocatedBytes);
    }

    [TestMethod]
    public async Task GetStackFramesAsync_ReturnsDefensiveRootToLeafFrames()
    {
        await using var temporaryStore = CreateTemporaryStore();
        var store = temporaryStore.Store;
        var rootFrameId = await AddFrameAsync(store, "Root", "App", "root");
        var leafFrameId = await AddFrameAsync(store, "Leaf", "App", "leaf");
        var rootStackId = await store.GetOrAddStackAsync(-1, rootFrameId, CancellationToken.None);
        var leafStackId = await store.GetOrAddStackAsync(rootStackId, leafFrameId, CancellationToken.None);

        var frames = await store.GetStackFramesAsync(leafStackId, CancellationToken.None);

        CollectionAssert.AreEqual(
            ExpectedRootToLeafFrameNames,
            frames.Select(frame => frame.Descriptor.MethodName).ToArray());
        Assert.IsFalse(frames is ExecutionFrameReference[]);
        var mutableFrames = (IList<ExecutionFrameReference>)frames;
        Assert.ThrowsExactly<NotSupportedException>(() => mutableFrames[0] = frames[1]);
    }

    private static ExecutionCallTreeNode AssertSingleChild(ExecutionCallTreeNode parent)
    {
        Assert.HasCount(1, parent.Children);
        return parent.Children[0];
    }

    private static async Task<int> AddFrameAsync(
        ExecutionCaptureStore store,
        string methodName,
        string moduleName,
        string symbolKey) =>
        await store.GetOrAddFrameAsync(
            new ExecutionFrameDescriptor(methodName, moduleName, $"C:\\{moduleName}.dll", symbolKey),
            CancellationToken.None);

    private static Task AppendAsync(ExecutionCaptureStore store, string time, int stackId) =>
        store.AppendAsync(
            new ExecutionSampleRecord(Instant(time), ThreadId: 7, stackId),
            CancellationToken.None).AsTask();

    private static ExecutionTimeRange Range(string start, string end) => new(Instant(start), Instant(end));

    private static DateTimeOffset Instant(string time) =>
        DateTimeOffset.ParseExact(
            $"2026-09-07T{time}Z",
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);

    private static async Task<long> MeasureBuildAllocatedBytesAsync(
        ExecutionProfileBuilder builder,
        ExecutionTimeRange range,
        ExecutionCaptureReadBoundary boundary)
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var profile = await builder.BuildAsync(range, boundary, lostEventCount: 0, CancellationToken.None);
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - before;
        GC.KeepAlive(profile);
        return allocatedBytes;
    }

    private static async Task IgnoreExpectedCancellationAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static TemporaryExecutionCaptureStore CreateTemporaryStore(
        Func<Task>? beforeStackFramesReadAsync = null,
        Func<Task>? afterDisposeWriterAcquiredAsync = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new TemporaryExecutionCaptureStore(
            root,
            new ExecutionCaptureStore(
                new ExecutionCaptureStorageLayout(root),
                beforeStackFramesReadAsync: beforeStackFramesReadAsync,
                afterDisposeWriterAcquiredAsync: afterDisposeWriterAcquiredAsync));
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

        public string Root => _root;

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
