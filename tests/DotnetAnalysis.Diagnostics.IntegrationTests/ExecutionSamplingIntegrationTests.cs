using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.DependencyInjection;
using DotnetAnalysis.Diagnostics.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class ExecutionSamplingIntegrationTests
{
    private const int QueryCancellationDelayMilliseconds = 100;
    private const int SnapshotCount = 3;
    private static readonly TimeSpan s_initialSamplingDuration = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan s_profileAvailabilityTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan s_profileRetryDelay = TimeSpan.FromMilliseconds(100);
    private readonly TestContext _testContext;

    public ExecutionSamplingIntegrationTests(TestContext testContext) => _testContext = testContext;

    [TestMethod]
    [DataRow("net8.0")]
    [DataRow("net9.0")]
    [DataRow("net10.0")]
    [TestCategory("WindowsDiagnosticsIntegration")]
    [DoNotParallelize]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task GetExecutionProfileAsync_OnSupportedAttachedTarget_RemainsContinuousAcrossQueriesAndSnapshots(
        string targetFramework)
    {
        EnsureRuntimeIsInstalled(targetFramework);
        var cancellationToken = _testContext.CancellationToken;
        await using var fixture = await AttachedExecutionTarget.StartAsync(
            targetFramework,
            copyWithoutPdb: false,
            cancellationToken);

        await WaitForInitialSamplesAsync(fixture.AttachedAtUtc, cancellationToken);
        var availableRange = CreateInitialAvailableRange(fixture.AttachedAtUtc);
        var randomRanges = CreateRandomRanges(availableRange, targetFramework);
        using var firstCancelledQuery = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var secondCancelledQuery = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var queryTasks = randomRanges
            .Select((range, index) => fixture.Session.GetExecutionProfileAsync(
                range,
                index switch
                {
                    0 => firstCancelledQuery.Token,
                    1 => secondCancelledQuery.Token,
                    _ => cancellationToken
                }))
            .ToArray();
        var firstQueryObservation = ObserveQueryCompletion(queryTasks[0]);
        var secondQueryObservation = ObserveQueryCompletion(queryTasks[1]);
        var firstCancellationScheduledAt = Stopwatch.GetTimestamp();
        firstCancelledQuery.CancelAfter(QueryCancellationDelayMilliseconds);
        var secondCancellationScheduledAt = Stopwatch.GetTimestamp();
        secondCancelledQuery.CancelAfter(QueryCancellationDelayMilliseconds);

        await AssertCancelledOrCompletedBeforeDeadlineAsync(
            firstQueryObservation,
            firstCancelledQuery,
            firstCancellationScheduledAt,
            cancellationToken);
        await AssertCancelledOrCompletedBeforeDeadlineAsync(
            secondQueryObservation,
            secondCancelledQuery,
            secondCancellationScheduledAt,
            cancellationToken);
        var successfulProfiles = await Task.WhenAll(queryTasks[2..]);

        Assert.HasCount(6, successfulProfiles);
        foreach (var profile in successfulProfiles)
        {
            AssertProfileCountsAreConsistent(profile);
        }

        var initialProfile = await fixture.Session.GetExecutionProfileAsync(availableRange, cancellationToken);
        AssertProfileCountsAreConsistent(initialProfile);
        AssertContainsExecutionWorkload(initialProfile);
        AssertContainsExistingWorkloadSource(initialProfile);

        var postQueryBaseline = await WaitForExpandedProfileAsync(
            fixture.Session,
            initialProfile,
            cancellationToken);
        AssertProfileCountsAreConsistent(postQueryBaseline);
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        var grownProfile = await WaitForExpandedProfileAsync(
            fixture.Session,
            postQueryBaseline,
            cancellationToken);
        AssertProfileCountsAreConsistent(grownProfile);
        Assert.IsGreaterThan(postQueryBaseline.ReceivedSampleCount, grownProfile.ReceivedSampleCount);

        var intervalStartAtUtc = grownProfile.TimeRange.EndAtUtc;
        MemorySnapshot? previousSnapshot = null;
        for (var snapshotIndex = 0; snapshotIndex < SnapshotCount; snapshotIndex++)
        {
            var beforeCapture = await WaitForNextNonEmptyProfileAsync(
                fixture.Session,
                intervalStartAtUtc,
                cancellationToken);
            Assert.AreEqual(ProcessDiagnosticsSessionState.Monitoring, fixture.Session.State);

            var snapshot = await fixture.Session.CaptureSnapshotAsync(cancellationToken);

            Assert.AreEqual(ProcessDiagnosticsSessionState.Monitoring, fixture.Session.State);
            Assert.AreEqual(MemorySnapshotOrigin.Captured, snapshot.Origin);
            Assert.AreEqual(MemorySnapshotState.Analyzing, snapshot.State);
            Assert.IsNotNull(snapshot.CaptureStartedAtUtc);
            Assert.IsNotNull(snapshot.CapturedAtUtc);
            Assert.IsGreaterThanOrEqualTo(snapshot.CaptureStartedAtUtc.Value, snapshot.CapturedAtUtc.Value);
            Assert.IsTrue(File.Exists(fixture.SnapshotLayout.GetFinalDumpPath(snapshot.Id)));
            if (previousSnapshot is not null)
            {
                Assert.IsGreaterThanOrEqualTo(
                    previousSnapshot.CapturedAtUtc!.Value,
                    snapshot.RequestedAtUtc);
            }

            var afterCapture = await WaitForNextNonEmptyProfileAsync(
                fixture.Session,
                beforeCapture.TimeRange.EndAtUtc,
                cancellationToken);
            Assert.AreEqual(beforeCapture.TimeRange.EndAtUtc, afterCapture.TimeRange.StartAtUtc);
            AssertProfileCountsAreConsistent(beforeCapture);
            AssertProfileCountsAreConsistent(afterCapture);
            Assert.AreEqual(ProcessDiagnosticsSessionState.Monitoring, fixture.Session.State);

            intervalStartAtUtc = afterCapture.TimeRange.EndAtUtc;
            previousSnapshot = snapshot;
        }

        await AssertRangeUnavailableAsync(
            fixture.Session,
            new ExecutionTimeRange(
                fixture.AttachRequestedAtUtc.AddTicks(-1),
                availableRange.StartAtUtc),
            cancellationToken);
        await AssertRangeUnavailableAsync(
            fixture.Session,
            new ExecutionTimeRange(
                availableRange.StartAtUtc,
                DateTimeOffset.UtcNow.AddMinutes(1)),
            cancellationToken);

        await fixture.Session.EndAsync(cancellationToken);
        Assert.AreEqual(ProcessDiagnosticsSessionState.Ended, fixture.Session.State);
        await AssertRangeUnavailableAsync(fixture.Session, availableRange, cancellationToken);
    }

    [TestMethod]
    [DataRow("net8.0")]
    [DataRow("net9.0")]
    [DataRow("net10.0")]
    [TestCategory("WindowsDiagnosticsIntegration")]
    [DoNotParallelize]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task GetExecutionProfileAsync_WhenTargetPdbIsExcluded_PreservesFramesWithoutSourceLocations(
        string targetFramework)
    {
        EnsureRuntimeIsInstalled(targetFramework);
        var cancellationToken = _testContext.CancellationToken;
        var fixture = await AttachedExecutionTarget.StartAsync(
            targetFramework,
            copyWithoutPdb: true,
            cancellationToken);
        var copiedOutputDirectory = Path.GetDirectoryName(fixture.TargetExecutablePath)
            ?? throw new AssertFailedException("The copied target executable must have an output directory.");
        var originalOutputDirectory = Path.GetDirectoryName(
            IntegrationTestHost.ResolveTargetExecutablePath(targetFramework));

        try
        {
            Assert.AreNotEqual(originalOutputDirectory, copiedOutputDirectory, ignoreCase: true);
            Assert.IsFalse(File.Exists(Path.Combine(
                copiedOutputDirectory,
                "DotnetAnalysis.Diagnostics.TestTarget.pdb")));

            await WaitForInitialSamplesAsync(fixture.AttachedAtUtc, cancellationToken);
            var profile = await fixture.Session.GetExecutionProfileAsync(
                CreateInitialAvailableRange(fixture.AttachedAtUtc),
                cancellationToken);

            AssertProfileCountsAreConsistent(profile);
            AssertContainsExecutionWorkload(profile);
            Assert.IsFalse(
                EnumerateFrames(profile).Any(static frame => frame.SourceLocation is not null),
                "A target copied without its PDB must preserve symbols while omitting source locations.");
        }
        finally
        {
            await fixture.DisposeAsync();
        }

        Assert.IsFalse(
            Directory.Exists(copiedOutputDirectory),
            "Disposing the integration host must remove its copied target output directory.");
    }

    private static void EnsureRuntimeIsInstalled(string targetFramework)
    {
        var runtimeMajorVersion = int.Parse(
            targetFramework.AsSpan("net".Length, targetFramework.IndexOf('.') - "net".Length),
            System.Globalization.CultureInfo.InvariantCulture);
        if (!IntegrationTestHost.GetInstalledRuntimeMajorVersions().Contains(runtimeMajorVersion))
        {
            Assert.Inconclusive($"{targetFramework} runtime is not installed on this machine.");
        }
    }

    private static async Task WaitForInitialSamplesAsync(
        DateTimeOffset attachedAtUtc,
        CancellationToken cancellationToken)
    {
        var remaining = attachedAtUtc + s_initialSamplingDuration - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }
    }

    private static ExecutionTimeRange CreateInitialAvailableRange(DateTimeOffset attachedAtUtc) =>
        new(
            attachedAtUtc.AddMilliseconds(250),
            attachedAtUtc.AddSeconds(5));

    private static ExecutionTimeRange[] CreateRandomRanges(
        ExecutionTimeRange availableRange,
        string targetFramework)
    {
        const int queryCount = 8;
        var random = new Random(StringComparer.Ordinal.GetHashCode(targetFramework));
        var availableTicks = availableRange.EndAtUtc.Ticks - availableRange.StartAtUtc.Ticks;
        var latestStartOffset = availableTicks / 5;
        var earliestEndOffset = availableTicks * 4 / 5;
        var ranges = new ExecutionTimeRange[queryCount];
        for (var index = 0; index < ranges.Length; index++)
        {
            var startOffset = random.NextInt64(latestStartOffset + 1);
            var endOffset = random.NextInt64(earliestEndOffset, availableTicks + 1);
            ranges[index] = new ExecutionTimeRange(
                availableRange.StartAtUtc.AddTicks(startOffset),
                availableRange.StartAtUtc.AddTicks(endOffset));
        }

        return ranges;
    }

    private static async Task<ExecutionProfile> WaitForExpandedProfileAsync(
        IProcessDiagnosticsSession session,
        ExecutionProfile previousProfile,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + s_profileAvailabilityTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var candidateEndAtUtc = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMilliseconds(250));
            if (candidateEndAtUtc > previousProfile.TimeRange.EndAtUtc)
            {
                try
                {
                    var profile = await session.GetExecutionProfileAsync(
                        new ExecutionTimeRange(previousProfile.TimeRange.StartAtUtc, candidateEndAtUtc),
                        cancellationToken);
                    if (profile.ReceivedSampleCount > previousProfile.ReceivedSampleCount)
                    {
                        return profile;
                    }
                }
                catch (DiagnosticsException exception) when (
                    exception.ErrorCode is DiagnosticsErrorCode.ExecutionProfileRangeUnavailable)
                {
                }
            }

            await Task.Delay(s_profileRetryDelay, cancellationToken);
        }

        throw new AssertFailedException("Execution sampling did not continue growing after concurrent queries.");
    }

    private static async Task<ExecutionProfile> WaitForNextNonEmptyProfileAsync(
        IProcessDiagnosticsSession session,
        DateTimeOffset startAtUtc,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + s_profileAvailabilityTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var candidateEndAtUtc = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMilliseconds(250));
            if (candidateEndAtUtc > startAtUtc)
            {
                try
                {
                    var profile = await session.GetExecutionProfileAsync(
                        new ExecutionTimeRange(startAtUtc, candidateEndAtUtc),
                        cancellationToken);
                    if (profile.ReceivedSampleCount > 0)
                    {
                        return profile;
                    }
                }
                catch (DiagnosticsException exception) when (
                    exception.ErrorCode is DiagnosticsErrorCode.ExecutionProfileRangeUnavailable)
                {
                }
            }

            await Task.Delay(s_profileRetryDelay, cancellationToken);
        }

        throw new AssertFailedException("Execution sampling did not produce a continuous non-empty interval.");
    }

    private static async Task AssertRangeUnavailableAsync(
        IProcessDiagnosticsSession session,
        ExecutionTimeRange range,
        CancellationToken cancellationToken)
    {
        var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(
            async () => await session.GetExecutionProfileAsync(range, cancellationToken));
        Assert.AreEqual(DiagnosticsErrorCode.ExecutionProfileRangeUnavailable, exception.ErrorCode);
    }

    private static Task<ObservedExecutionQuery> ObserveQueryCompletion(Task<ExecutionProfile> queryTask) =>
        queryTask.ContinueWith(
            static completedQuery => new ObservedExecutionQuery(completedQuery, Stopwatch.GetTimestamp()),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static async Task AssertCancelledOrCompletedBeforeDeadlineAsync(
        Task<ObservedExecutionQuery> observationTask,
        CancellationTokenSource queryCancellation,
        long cancellationScheduledAt,
        CancellationToken testCancellation)
    {
        var observation = await observationTask;
        var completionDelay = Stopwatch.GetElapsedTime(
            cancellationScheduledAt,
            observation.CompletedAtTimestamp);
        if (observation.QueryTask.IsCompletedSuccessfully)
        {
            Assert.IsTrue(
                completionDelay <= TimeSpan.FromMilliseconds(QueryCancellationDelayMilliseconds),
                $"A cancellable execution query completed successfully after its {QueryCancellationDelayMilliseconds} ms deadline.");
            AssertProfileCountsAreConsistent(observation.QueryTask.Result);
            return;
        }

        try
        {
            await observation.QueryTask;
            Assert.Fail("The observed execution query did not produce a result or cancellation.");
        }
        catch (OperationCanceledException) when (!testCancellation.IsCancellationRequested)
        {
            Assert.IsTrue(
                queryCancellation.IsCancellationRequested,
                "The execution query cancellation must originate from its scheduled query token.");
            Assert.IsTrue(
                completionDelay >= TimeSpan.FromMilliseconds(QueryCancellationDelayMilliseconds),
                "Only an execution query that remained active until its cancellation deadline may report cancellation.");
        }
    }

    private static void AssertProfileCountsAreConsistent(ExecutionProfile profile)
    {
        Assert.IsGreaterThan(0L, profile.ReceivedSampleCount);
        Assert.IsNotEmpty(profile.Hotspots);
        Assert.IsNotEmpty(profile.CallTreeRoots);
        Assert.AreEqual(
            profile.ReceivedSampleCount,
            profile.Hotspots.Sum(static hotspot => hotspot.ExclusiveSampleCount));
        Assert.AreEqual(
            profile.ReceivedSampleCount,
            profile.CallTreeRoots.Sum(static root => root.InclusiveSampleCount));

        for (var index = 1; index < profile.Hotspots.Count; index++)
        {
            Assert.IsGreaterThanOrEqualTo(
                profile.Hotspots[index].InclusiveSampleCount,
                profile.Hotspots[index - 1].InclusiveSampleCount,
                "Execution hotspots must be sorted by inclusive sample count descending.");
        }
    }

    private static void AssertContainsExecutionWorkload(ExecutionProfile profile)
    {
        Assert.IsTrue(
            profile.Hotspots.Any(static hotspot =>
                hotspot.Frame.MethodName.Contains("ExecutionSamplingWorkload", StringComparison.Ordinal)),
            "Execution hotspots must contain the controlled execution workload.");
        Assert.IsTrue(
            EnumerateCallTreeNodes(profile.CallTreeRoots).Any(static node =>
                node.Frame.MethodName.Contains("ExecutionSamplingWorkload", StringComparison.Ordinal)),
            "The execution call tree must contain the controlled execution workload.");
    }

    private static void AssertContainsExistingWorkloadSource(ExecutionProfile profile)
    {
        var sourceLocation = EnumerateFrames(profile)
            .Where(static frame => frame.MethodName.Contains("ExecutionSamplingWorkload", StringComparison.Ordinal))
            .Select(static frame => frame.SourceLocation)
            .FirstOrDefault(static source =>
                source is not null
                && string.Equals(
                    Path.GetFileName(source.FilePath),
                    "ExecutionSamplingWorkload.cs",
                    StringComparison.OrdinalIgnoreCase));

        Assert.IsNotNull(sourceLocation, "Debug target symbols must resolve the workload source file.");
        Assert.IsTrue(File.Exists(sourceLocation.FilePath));
        Assert.IsGreaterThan(0, sourceLocation.LineNumber);
    }

    private static IEnumerable<ExecutionFrame> EnumerateFrames(ExecutionProfile profile)
    {
        foreach (var hotspot in profile.Hotspots)
        {
            yield return hotspot.Frame;
        }

        foreach (var node in EnumerateCallTreeNodes(profile.CallTreeRoots))
        {
            yield return node.Frame;
        }
    }

    private static IEnumerable<ExecutionCallTreeNode> EnumerateCallTreeNodes(
        IEnumerable<ExecutionCallTreeNode> roots)
    {
        var pending = new Stack<ExecutionCallTreeNode>(roots.Reverse());
        while (pending.TryPop(out var node))
        {
            yield return node;
            for (var index = node.Children.Count - 1; index >= 0; index--)
            {
                pending.Push(node.Children[index]);
            }
        }
    }

    private sealed record ObservedExecutionQuery(
        Task<ExecutionProfile> QueryTask,
        long CompletedAtTimestamp);

    private sealed class AttachedExecutionTarget : IAsyncDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly IntegrationTestHost _target;
        private readonly string _snapshotRoot;

        private AttachedExecutionTarget(
            IntegrationTestHost target,
            ServiceProvider serviceProvider,
            IProcessDiagnosticsSession session,
            SnapshotStorageLayout snapshotLayout,
            string snapshotRoot,
            string targetExecutablePath,
            DateTimeOffset attachRequestedAtUtc,
            DateTimeOffset attachedAtUtc)
        {
            _target = target;
            _serviceProvider = serviceProvider;
            Session = session;
            SnapshotLayout = snapshotLayout;
            _snapshotRoot = snapshotRoot;
            TargetExecutablePath = targetExecutablePath;
            AttachRequestedAtUtc = attachRequestedAtUtc;
            AttachedAtUtc = attachedAtUtc;
        }

        public IProcessDiagnosticsSession Session { get; }

        public SnapshotStorageLayout SnapshotLayout { get; }

        public string TargetExecutablePath { get; }

        public DateTimeOffset AttachRequestedAtUtc { get; }

        public DateTimeOffset AttachedAtUtc { get; }

        public static async Task<AttachedExecutionTarget> StartAsync(
            string targetFramework,
            bool copyWithoutPdb,
            CancellationToken cancellationToken)
        {
            var target = await IntegrationTestHost.StartTargetAsync(
                targetFramework,
                new IntegrationTargetOptions(
                    EnableExecutionWorkload: true,
                    CopyWithoutPdb: copyWithoutPdb));
            var snapshotRoot = Path.Combine(
                Path.GetTempPath(),
                "DotnetAnalysis.Diagnostics.IntegrationTests",
                "ExecutionSnapshots",
                Guid.NewGuid().ToString("N"));
            ServiceProvider? serviceProvider = null;
            IProcessDiagnosticsSession? session = null;
            try
            {
                var snapshotLayout = new SnapshotStorageLayout(snapshotRoot);
                var services = new ServiceCollection();
                services.AddSingleton<IEventBus>(_ =>
                    new InProcessEventBus(NullLogger<InProcessEventBus>.Instance));
                services.AddSingleton(snapshotLayout);
                services.AddWindowsProcessDiagnostics();
                serviceProvider = services.BuildServiceProvider(validateScopes: true);
                var diagnostics = serviceProvider.GetRequiredService<IProcessDiagnostics>();
                Assert.IsInstanceOfType<WindowsProcessDiagnostics>(diagnostics);
                var process = (await diagnostics.GetProcessesAsync(cancellationToken))
                    .Single(candidate => candidate.ProcessId == target.ProcessId);
                var attachRequestedAtUtc = DateTimeOffset.UtcNow;
                session = await diagnostics.AttachAsync(process, cancellationToken);
                var attachedAtUtc = DateTimeOffset.UtcNow;
                Assert.IsInstanceOfType<ProcessDiagnosticsSession>(session);

                return new AttachedExecutionTarget(
                    target,
                    serviceProvider,
                    session,
                    snapshotLayout,
                    snapshotRoot,
                    GetExecutablePath(target.ProcessId),
                    attachRequestedAtUtc,
                    attachedAtUtc);
            }
            catch
            {
                if (session is not null)
                {
                    await session.DisposeAsync();
                }

                if (serviceProvider is not null)
                {
                    await serviceProvider.DisposeAsync();
                }

                await target.DisposeAsync();
                DeleteDirectoryIfPresent(snapshotRoot);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Session.DisposeAsync();
            }
            finally
            {
                try
                {
                    await _serviceProvider.DisposeAsync();
                }
                finally
                {
                    try
                    {
                        await _target.DisposeAsync();
                    }
                    finally
                    {
                        DeleteDirectoryIfPresent(_snapshotRoot);
                    }
                }
            }
        }

        private static string GetExecutablePath(int processId)
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName
                ?? throw new AssertFailedException("The integration target executable path must be available.");
        }

        private static void DeleteDirectoryIfPresent(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }
}
