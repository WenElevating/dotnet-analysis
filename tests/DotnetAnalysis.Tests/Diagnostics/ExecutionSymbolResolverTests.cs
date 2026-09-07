using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Tests.Diagnostics;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class ExecutionSymbolResolverTests
{
    [TestMethod]
    public async Task ResolveAsync_ConcurrentRequestsForSameFrame_PerformsOneLookup()
    {
        var sourcePath = CreateTemporarySourceFile();
        try
        {
            var lookup = new ControlledSourceLocationLookup(
                new ExecutionSourceLocationLookupResult(
                    ExecutionSourceLocationLookupStatus.Found,
                    sourcePath,
                    LineNumber: 17,
                    ColumnNumber: 5),
                waitForRelease: true);
            await using var resolver = new ExecutionSymbolResolver(lookup);
            var frame = Frame(frameId: 7);

            var first = resolver.ResolveAsync(frame, CancellationToken.None);
            await lookup.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var second = resolver.ResolveAsync(frame, CancellationToken.None);
            lookup.Release.TrySetResult();

            var locations = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(1));

            Assert.AreEqual(1, lookup.CallCount);
            Assert.IsNotNull(locations[0]);
            Assert.AreEqual(Path.GetFullPath(sourcePath), locations[0]!.FilePath);
            Assert.AreEqual(17, locations[0]!.LineNumber);
            Assert.AreEqual(5, locations[0]!.ColumnNumber);
            Assert.AreSame(locations[0], locations[1]);
        }
        finally
        {
            DeleteTemporarySourceFile(sourcePath);
        }
    }

    [TestMethod]
    [DataRow("dynamic-module")]
    [DataRow("pdb-missing")]
    [DataRow("pdb-mismatch")]
    public async Task ResolveAsync_WhenLocalSymbolsAreUnavailable_ReturnsNull(string statusName)
    {
        var status = statusName switch
        {
            "dynamic-module" => ExecutionSourceLocationLookupStatus.DynamicModule,
            "pdb-missing" => ExecutionSourceLocationLookupStatus.PdbMissing,
            "pdb-mismatch" => ExecutionSourceLocationLookupStatus.PdbMismatch,
            _ => throw new ArgumentOutOfRangeException(nameof(statusName), statusName, null)
        };
        var lookup = new ControlledSourceLocationLookup(
            new ExecutionSourceLocationLookupResult(status, null, LineNumber: 0, ColumnNumber: 0));
        await using var resolver = new ExecutionSymbolResolver(lookup);

        var location = await resolver.ResolveAsync(Frame(frameId: 11), CancellationToken.None);

        Assert.IsNull(location);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenBuildTimeSourceFileIsMissing_ReturnsNull()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.cs");
        var lookup = new ControlledSourceLocationLookup(
            new ExecutionSourceLocationLookupResult(
                ExecutionSourceLocationLookupStatus.Found,
                missingPath,
                LineNumber: 23,
                ColumnNumber: 4));
        await using var resolver = new ExecutionSymbolResolver(lookup);

        var location = await resolver.ResolveAsync(Frame(frameId: 13), CancellationToken.None);

        Assert.IsNull(location);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenLineNumberIsNotPositive_ReturnsNull()
    {
        var sourcePath = CreateTemporarySourceFile();
        try
        {
            var lookup = new ControlledSourceLocationLookup(
                new ExecutionSourceLocationLookupResult(
                    ExecutionSourceLocationLookupStatus.Found,
                    sourcePath,
                    LineNumber: 0,
                    ColumnNumber: 4));
            await using var resolver = new ExecutionSymbolResolver(lookup);

            var location = await resolver.ResolveAsync(Frame(frameId: 17), CancellationToken.None);

            Assert.IsNull(location);
        }
        finally
        {
            DeleteTemporarySourceFile(sourcePath);
        }
    }

    [TestMethod]
    public async Task ResolveAsync_WhenLookupThrows_ReturnsNullAndCachesFallback()
    {
        var lookup = new ControlledSourceLocationLookup(new IOException("PDB read failed."));
        await using var resolver = new ExecutionSymbolResolver(lookup);
        var frame = Frame(frameId: 19);

        var first = await resolver.ResolveAsync(frame, CancellationToken.None);
        var second = await resolver.ResolveAsync(frame, CancellationToken.None);

        Assert.IsNull(first);
        Assert.IsNull(second);
        Assert.AreEqual(1, lookup.CallCount);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenOneWaitIsCancelled_DoesNotCancelSharedLookup()
    {
        var sourcePath = CreateTemporarySourceFile();
        try
        {
            var lookup = new ControlledSourceLocationLookup(
                new ExecutionSourceLocationLookupResult(
                    ExecutionSourceLocationLookupStatus.Found,
                    sourcePath,
                    LineNumber: 31,
                    ColumnNumber: 0),
                waitForRelease: true);
            await using var resolver = new ExecutionSymbolResolver(lookup);
            var frame = Frame(frameId: 23);
            using var cancellation = new CancellationTokenSource();

            var cancelledWait = resolver.ResolveAsync(frame, cancellation.Token);
            await lookup.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var survivingWait = resolver.ResolveAsync(frame, CancellationToken.None);
            cancellation.Cancel();

            try
            {
                await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelledWait);
            }
            finally
            {
                lookup.Release.TrySetResult();
            }

            var location = await survivingWait.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.IsNotNull(location);
            Assert.AreEqual(31, location.LineNumber);
            Assert.IsNull(location.ColumnNumber);
            Assert.AreEqual(1, lookup.CallCount);
        }
        finally
        {
            DeleteTemporarySourceFile(sourcePath);
        }
    }

    private static ExecutionFrameReference Frame(int frameId) => new(
        frameId,
        new ExecutionFrameDescriptor("Worker.Run", "Worker", "C:\\Worker.dll", $"symbol-{frameId}"),
        SymbolAddress: null);

    private static string CreateTemporarySourceFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"), "Worker.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "internal static class Worker { }");
        return path;
    }

    private static void DeleteTemporarySourceFile(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ControlledSourceLocationLookup : IExecutionSourceLocationLookup
    {
        private readonly ExecutionSourceLocationLookupResult? _result;
        private readonly Exception? _exception;
        private readonly bool _waitForRelease;
        private int _callCount;

        public ControlledSourceLocationLookup(
            ExecutionSourceLocationLookupResult result,
            bool waitForRelease = false)
        {
            _result = result;
            _waitForRelease = waitForRelease;
        }

        public ControlledSourceLocationLookup(Exception exception)
        {
            _exception = exception;
        }

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public ExecutionSourceLocationLookupResult Lookup(ExecutionFrameReference frame)
        {
            _ = frame;
            Interlocked.Increment(ref _callCount);
            Started.TrySetResult();
            if (_waitForRelease)
            {
                Release.Task.GetAwaiter().GetResult();
            }

            if (_exception is not null)
            {
                throw _exception;
            }

            return _result!;
        }
    }
}
