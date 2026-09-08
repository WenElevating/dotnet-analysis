using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;
using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetAnalysis.Tests.Diagnostics;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class ExecutionSamplingSessionTests
{
    [TestMethod]
    [DataRow("diagnostics-client", 2, 1)]
    [DataRow("io", 2, 1)]
    [DataRow("unauthorized", 1, 0)]
    public async Task StartAsync_WhenEventPipeStartFails_RecordsStableUnavailableError(
        string failureKind,
        int expectedStartCalls,
        int expectedDelayCalls)
    {
        var failures = Enumerable.Range(0, expectedStartCalls)
            .Select(_ => CreateStartFailure(failureKind))
            .ToArray();
        var sampler = new ControlledEventPipeExecutionSampler(failures);
        var delays = new List<TimeSpan>();
        await using var fixture = CreateFixture(sampler, (delay, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            delays.Add(delay);
            return Task.CompletedTask;
        });

        await fixture.Session.StartAsync(fixture.Target, CancellationToken.None);
        var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(async () =>
            await fixture.Session.GetExecutionProfileAsync(
                new ExecutionTimeRange(fixture.StartedAtUtc, fixture.StartedAtUtc.AddSeconds(1)),
                CancellationToken.None));

        Assert.AreEqual(DiagnosticsErrorCode.ExecutionProfilingUnavailable, exception.ErrorCode);
        Assert.IsInstanceOfType(exception.InnerException, failures[^1].GetType());
        Assert.AreEqual(expectedStartCalls, sampler.StartCallCount);
        Assert.HasCount(expectedDelayCalls, delays);
        if (expectedDelayCalls != 0)
        {
            Assert.AreEqual(TimeSpan.FromSeconds(1), delays[0]);
        }
    }

    [TestMethod]
    public async Task StartAsync_WhenTransientFailureRecovers_RetriesOnceAndStartsSampling()
    {
        var sampler = new ControlledEventPipeExecutionSampler([new IOException("first attempt")]);
        var delays = new List<TimeSpan>();
        await using var fixture = CreateFixture(sampler, (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

        await fixture.Session.StartAsync(fixture.Target, CancellationToken.None);

        Assert.IsTrue(sampler.IsRunning);
        Assert.AreEqual(2, sampler.StartCallCount);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1) }, delays);
    }

    [TestMethod]
    public async Task StartAsync_WhenNestedAggregateWrapsTransientFailures_RetriesOnceAndPreservesWrapper()
    {
        var firstFailure = new AggregateException(
            "first wrapper",
            new AggregateException(new DiagnosticsClientException("first attempt")));
        var secondFailure = new AggregateException(
            "second wrapper",
            new AggregateException(new IOException("second attempt")));
        var sampler = new ControlledEventPipeExecutionSampler([firstFailure, secondFailure]);
        var delays = new List<TimeSpan>();
        await using var fixture = CreateFixture(sampler, (delay, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            delays.Add(delay);
            return Task.CompletedTask;
        });

        await fixture.Session.StartAsync(fixture.Target, CancellationToken.None);
        var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(async () =>
            await fixture.Session.GetExecutionProfileAsync(
                new ExecutionTimeRange(fixture.StartedAtUtc, fixture.StartedAtUtc.AddSeconds(1)),
                CancellationToken.None));

        Assert.AreEqual(DiagnosticsErrorCode.ExecutionProfilingUnavailable, exception.ErrorCode);
        Assert.AreSame(secondFailure, exception.InnerException);
        Assert.AreEqual(2, sampler.StartCallCount);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1) }, delays);
    }

    [TestMethod]
    public async Task StartAsync_WhenRetryWaitIsCancelled_AllowsLaterStart()
    {
        var sampler = new ControlledEventPipeExecutionSampler([new IOException("first attempt")]);
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = CreateFixture(sampler, (_, cancellationToken) =>
        {
            delayStarted.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        using var cancellation = new CancellationTokenSource();

        var cancelledStart = fixture.Session.StartAsync(fixture.Target, cancellation.Token);
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelledStart);
        await fixture.Session.StartAsync(fixture.Target, CancellationToken.None);

        Assert.IsTrue(sampler.IsRunning);
        Assert.AreEqual(2, sampler.StartCallCount);
    }

    [TestMethod]
    public async Task DisposeAsync_AfterStartWasCancelled_DoesNotRepeatOperationFailure()
    {
        var sampler = new ControlledEventPipeExecutionSampler([new IOException("first attempt")]);
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = CreateFixture(sampler, (_, cancellationToken) =>
        {
            delayStarted.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        using var cancellation = new CancellationTokenSource();

        var cancelledStart = fixture.Session.StartAsync(fixture.Target, cancellation.Token);
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelledStart);

        await fixture.DisposeAsync();

        Assert.AreEqual(1, sampler.StopCallCount);
        Assert.AreEqual(1, sampler.DisposeCallCount);
        Assert.IsFalse(Directory.Exists(fixture.SessionDirectory));
    }

    [TestMethod]
    public async Task DisposeAsync_WhenConcurrentStartWaitIsCancelled_StillCompletesCleanup()
    {
        var sampler = new ControlledEventPipeExecutionSampler([new IOException("first attempt")]);
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = CreateFixture(sampler, (_, cancellationToken) =>
        {
            delayStarted.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        using var cancellation = new CancellationTokenSource();

        try
        {
            var cancelledStart = fixture.Session.StartAsync(fixture.Target, cancellation.Token);
            await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var dispose = fixture.Session.DisposeAsync().AsTask();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelledStart);
            await dispose.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.AreEqual(1, sampler.StopCallCount);
            Assert.AreEqual(1, sampler.DisposeCallCount);
            Assert.IsFalse(Directory.Exists(fixture.SessionDirectory));
        }
        finally
        {
            cancellation.Cancel();
            await fixture.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task GetExecutionProfileAsync_WhenSamplerReportsStorageFailure_ThrowsStableStorageError()
    {
        var storageFailure = new DiagnosticsException(
            DiagnosticsErrorCode.ExecutionProfileStorageFailed,
            "Execution sample could not be committed.",
            new IOException("disk full"));
        var sampler = new ControlledEventPipeExecutionSampler { TerminalFailure = storageFailure };
        await using var fixture = CreateFixture(sampler);
        await fixture.Session.StartAsync(fixture.Target, CancellationToken.None);

        var exception = await Assert.ThrowsExactlyAsync<DiagnosticsException>(async () =>
            await fixture.Session.GetExecutionProfileAsync(
                new ExecutionTimeRange(fixture.StartedAtUtc, fixture.StartedAtUtc.AddSeconds(1)),
                CancellationToken.None));

        Assert.AreSame(storageFailure, exception);
        Assert.AreEqual(DiagnosticsErrorCode.ExecutionProfileStorageFailed, exception.ErrorCode);
    }

    [TestMethod]
    public async Task GetExecutionProfileAsync_UsesFixedBoundaryAndLostEventSnapshot()
    {
        var sampler = new ControlledEventPipeExecutionSampler { LostEventCount = 9 };
        await using var fixture = CreateFixture(sampler);
        await fixture.Session.StartAsync(fixture.Target, CancellationToken.None);
        await AppendSamplesAsync(fixture.Store, fixture.StartedAtUtc, 1, 2, 3);

        var profile = await fixture.Session.GetExecutionProfileAsync(
            new ExecutionTimeRange(fixture.StartedAtUtc, fixture.StartedAtUtc.AddSeconds(3)),
            CancellationToken.None);

        Assert.AreEqual(2, profile.ReceivedSampleCount);
        Assert.AreEqual(9, profile.LostEventCount);
        Assert.HasCount(1, profile.Hotspots);
        Assert.AreEqual(2, profile.Hotspots[0].InclusiveSampleCount);
    }

    [TestMethod]
    public async Task GetExecutionProfileAsync_ExplicitModesProduceEquivalentProfiles()
    {
        var sampler = new ControlledEventPipeExecutionSampler { LostEventCount = 9 };
        await using var fixture = CreateFixture(sampler);
        await fixture.Session.StartAsync(fixture.Target, CancellationToken.None);
        await AppendSamplesAsync(fixture.Store, fixture.StartedAtUtc, 1, 2, 3);
        var range = new ExecutionTimeRange(fixture.StartedAtUtc, fixture.StartedAtUtc.AddSeconds(3));

        var fullScan = await fixture.Session.GetExecutionProfileAsync(
            range,
            ExecutionProfileQueryMode.FullScan,
            CancellationToken.None);
        var incremental = await fixture.Session.GetExecutionProfileAsync(
            range,
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None);

        Assert.AreEqual(fullScan.TimeRange, incremental.TimeRange);
        Assert.AreEqual(fullScan.ReceivedSampleCount, incremental.ReceivedSampleCount);
        Assert.AreEqual(fullScan.LostEventCount, incremental.LostEventCount);
        Assert.HasCount(fullScan.Hotspots.Count, incremental.Hotspots);
        Assert.HasCount(fullScan.CallTreeRoots.Count, incremental.CallTreeRoots);
        for (var index = 0; index < fullScan.Hotspots.Count; index++)
        {
            Assert.AreEqual(fullScan.Hotspots[index], incremental.Hotspots[index]);
        }

        for (var index = 0; index < fullScan.CallTreeRoots.Count; index++)
        {
            AssertCallTreeNodesEqual(fullScan.CallTreeRoots[index], incremental.CallTreeRoots[index]);
        }
    }

    [TestMethod]
    public async Task GetExecutionProfileAsync_ConcurrentIncrementalQueriesShareBuildAndCancellationOnlyStopsWaiter()
    {
        var stackReadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStackRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stackReadCount = 0;
        async Task BlockFirstStackReadAsync()
        {
            if (Interlocked.Increment(ref stackReadCount) == 1)
            {
                stackReadEntered.TrySetResult();
                await releaseStackRead.Task;
            }
        }

        var sampler = new ControlledEventPipeExecutionSampler();
        await using var fixture = CreateFixture(sampler, beforeStackFramesReadAsync: BlockFirstStackReadAsync);
        await fixture.Session.StartAsync(fixture.Target, CancellationToken.None);
        await AppendSamplesAsync(fixture.Store, fixture.StartedAtUtc, 1, 2);
        var range = new ExecutionTimeRange(fixture.StartedAtUtc, fixture.StartedAtUtc.AddSeconds(2));
        using var firstCancellation = new CancellationTokenSource();

        var cancelledWaiter = fixture.Session.GetExecutionProfileAsync(
            range,
            ExecutionProfileQueryMode.Incremental,
            firstCancellation.Token);
        await stackReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var survivingWaiter = fixture.Session.GetExecutionProfileAsync(
            range,
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None);
        firstCancellation.Cancel();

        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelledWaiter);
            Assert.AreEqual(1, Volatile.Read(ref stackReadCount));
        }
        finally
        {
            releaseStackRead.TrySetResult();
        }

        var profile = await survivingWaiter.WaitAsync(TimeSpan.FromSeconds(1));
        var cachedProfile = await fixture.Session.GetExecutionProfileAsync(
            range,
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None);

        Assert.AreEqual(1, profile.ReceivedSampleCount);
        Assert.AreSame(profile, cachedProfile);
        Assert.AreEqual(1, Volatile.Read(ref stackReadCount));
    }

    [TestMethod]
    public async Task GetExecutionProfileAsync_DifferentIncrementalRangesBuildAtMostTwoAtATime()
    {
        var firstTwoBuildsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unexpectedThirdBuildEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBuilds = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredBuildCount = 0;
        var activeBuildCount = 0;
        var maximumActiveBuildCount = 0;
        async Task BlockBuildAsync()
        {
            var entered = Interlocked.Increment(ref enteredBuildCount);
            var active = Interlocked.Increment(ref activeBuildCount);
            UpdateMaximum(ref maximumActiveBuildCount, active);
            if (entered == 2)
            {
                firstTwoBuildsEntered.TrySetResult();
            }
            else if (entered == 3)
            {
                unexpectedThirdBuildEntered.TrySetResult();
            }

            try
            {
                await releaseBuilds.Task;
            }
            finally
            {
                Interlocked.Decrement(ref activeBuildCount);
            }
        }

        var sampler = new ControlledEventPipeExecutionSampler();
        await using var fixture = CreateFixture(sampler, beforeStackFramesReadAsync: BlockBuildAsync);
        await fixture.Session.StartAsync(fixture.Target, CancellationToken.None);
        await AppendSamplesAsync(fixture.Store, fixture.StartedAtUtc, 1, 2, 3, 4);
        var ranges = new[]
        {
            new ExecutionTimeRange(fixture.StartedAtUtc, fixture.StartedAtUtc.AddSeconds(2)),
            new ExecutionTimeRange(fixture.StartedAtUtc, fixture.StartedAtUtc.AddSeconds(3)),
            new ExecutionTimeRange(fixture.StartedAtUtc, fixture.StartedAtUtc.AddSeconds(4))
        };

        var queries = ranges
            .Select(range => fixture.Session.GetExecutionProfileAsync(
                range,
                ExecutionProfileQueryMode.Incremental,
                CancellationToken.None))
            .ToArray();
        await firstTwoBuildsEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var sameKeyWaiter = fixture.Session.GetExecutionProfileAsync(
            ranges[0],
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None);

        try
        {
            var completedTask = await Task.WhenAny(
                unexpectedThirdBuildEntered.Task,
                Task.Delay(TimeSpan.FromMilliseconds(250)));

            Assert.AreNotSame(unexpectedThirdBuildEntered.Task, completedTask);
            Assert.AreEqual(2, Volatile.Read(ref enteredBuildCount));
            Assert.AreEqual(2, Volatile.Read(ref maximumActiveBuildCount));
        }
        finally
        {
            releaseBuilds.TrySetResult();
        }

        var profiles = await Task.WhenAll(queries).WaitAsync(TimeSpan.FromSeconds(1));
        var sameKeyProfile = await sameKeyWaiter.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreSame(profiles[0], sameKeyProfile);
        Assert.AreEqual(3, Volatile.Read(ref enteredBuildCount));
        Assert.IsLessThanOrEqualTo(2, Volatile.Read(ref maximumActiveBuildCount));
    }

    [TestMethod]
    public async Task GetExecutionProfileAsync_SealedHistoryRemainsCachedAfterUnrelatedActiveAppend()
    {
        var stackReadCount = 0;
        var sampler = new ControlledEventPipeExecutionSampler();
        await using var fixture = CreateFixture(
            sampler,
            beforeStackFramesReadAsync: () =>
            {
                Interlocked.Increment(ref stackReadCount);
                return Task.CompletedTask;
            },
            segmentDataLimitBytes: 3);
        await fixture.Session.StartAsync(fixture.Target, CancellationToken.None);
        await AppendSamplesAsync(fixture.Store, fixture.StartedAtUtc, 1, 2);
        var historicalRange = new ExecutionTimeRange(
            fixture.StartedAtUtc.AddSeconds(1),
            fixture.StartedAtUtc.AddSeconds(2));

        var firstProfile = await fixture.Session.GetExecutionProfileAsync(
            historicalRange,
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None);
        await AppendSamplesAsync(fixture.Store, fixture.StartedAtUtc, 3);
        var cachedProfile = await fixture.Session.GetExecutionProfileAsync(
            historicalRange,
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None);

        Assert.AreSame(firstProfile, cachedProfile);
        Assert.AreEqual(1, Volatile.Read(ref stackReadCount));
    }

    [TestMethod]
    public async Task GetExecutionProfileAsync_ActiveRangeRebuildsWhenItsReadBoundaryChanges()
    {
        var stackReadCount = 0;
        var sampler = new ControlledEventPipeExecutionSampler();
        await using var fixture = CreateFixture(
            sampler,
            beforeStackFramesReadAsync: () =>
            {
                Interlocked.Increment(ref stackReadCount);
                return Task.CompletedTask;
            },
            segmentDataLimitBytes: 12);
        await fixture.Session.StartAsync(fixture.Target, CancellationToken.None);
        await AppendSamplesAsync(fixture.Store, fixture.StartedAtUtc, 1, 2);
        var activeRange = new ExecutionTimeRange(
            fixture.StartedAtUtc.AddSeconds(1),
            fixture.StartedAtUtc.AddSeconds(2));

        var firstProfile = await fixture.Session.GetExecutionProfileAsync(
            activeRange,
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None);
        await AppendSamplesAsync(fixture.Store, fixture.StartedAtUtc, 3);
        var rebuiltProfile = await fixture.Session.GetExecutionProfileAsync(
            activeRange,
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None);

        Assert.AreNotSame(firstProfile, rebuiltProfile);
        Assert.AreEqual(2, Volatile.Read(ref stackReadCount));
    }

    [TestMethod]
    public async Task GetExecutionProfileAsync_LateSampleInHistoricalRangeDoesNotReuseSealedProfile()
    {
        var stackReadCount = 0;
        var sampler = new ControlledEventPipeExecutionSampler();
        await using var fixture = CreateFixture(
            sampler,
            beforeStackFramesReadAsync: () =>
            {
                Interlocked.Increment(ref stackReadCount);
                return Task.CompletedTask;
            },
            segmentDataLimitBytes: 3);
        await fixture.Session.StartAsync(fixture.Target, CancellationToken.None);
        await AppendSamplesAsync(fixture.Store, fixture.StartedAtUtc, 1, 2, 3);
        var historicalRange = new ExecutionTimeRange(
            fixture.StartedAtUtc.AddSeconds(1),
            fixture.StartedAtUtc.AddSeconds(2));

        var firstProfile = await fixture.Session.GetExecutionProfileAsync(
            historicalRange,
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None);
        await AppendSamplesAsync(fixture.Store, fixture.StartedAtUtc, 1);
        var rebuiltProfile = await fixture.Session.GetExecutionProfileAsync(
            historicalRange,
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None);

        Assert.AreNotSame(firstProfile, rebuiltProfile);
        Assert.AreEqual(2, rebuiltProfile.ReceivedSampleCount);
        Assert.AreEqual(2, Volatile.Read(ref stackReadCount));
    }

    [TestMethod]
    public async Task GetExecutionProfileAsync_ChangedLostEventCountDoesNotReuseCachedProfile()
    {
        var stackReadCount = 0;
        var sampler = new ControlledEventPipeExecutionSampler();
        await using var fixture = CreateFixture(
            sampler,
            beforeStackFramesReadAsync: () =>
            {
                Interlocked.Increment(ref stackReadCount);
                return Task.CompletedTask;
            });
        await fixture.Session.StartAsync(fixture.Target, CancellationToken.None);
        await AppendSamplesAsync(fixture.Store, fixture.StartedAtUtc, 1, 2);
        var range = new ExecutionTimeRange(fixture.StartedAtUtc, fixture.StartedAtUtc.AddSeconds(2));

        var firstProfile = await fixture.Session.GetExecutionProfileAsync(
            range,
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None);
        sampler.LostEventCount = 1;
        var rebuiltProfile = await fixture.Session.GetExecutionProfileAsync(
            range,
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None);

        Assert.AreNotSame(firstProfile, rebuiltProfile);
        Assert.AreEqual(1, rebuiltProfile.LostEventCount);
        Assert.AreEqual(2, Volatile.Read(ref stackReadCount));
    }

    [TestMethod]
    public async Task GetExecutionProfileAsync_FullScanQueriesDoNotShareCachedBuilds()
    {
        var firstStackReadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStackReadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstStackRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stackReadCount = 0;
        async Task BlockFirstStackReadAsync()
        {
            var currentCount = Interlocked.Increment(ref stackReadCount);
            if (currentCount == 1)
            {
                firstStackReadEntered.TrySetResult();
                await releaseFirstStackRead.Task;
            }
            else if (currentCount == 2)
            {
                secondStackReadEntered.TrySetResult();
            }
        }

        var sampler = new ControlledEventPipeExecutionSampler();
        await using var fixture = CreateFixture(sampler, beforeStackFramesReadAsync: BlockFirstStackReadAsync);
        await fixture.Session.StartAsync(fixture.Target, CancellationToken.None);
        await AppendSamplesAsync(fixture.Store, fixture.StartedAtUtc, 1, 2);
        var range = new ExecutionTimeRange(fixture.StartedAtUtc, fixture.StartedAtUtc.AddSeconds(2));

        var firstQuery = fixture.Session.GetExecutionProfileAsync(
            range,
            ExecutionProfileQueryMode.FullScan,
            CancellationToken.None);
        await firstStackReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var secondQuery = fixture.Session.GetExecutionProfileAsync(
            range,
            ExecutionProfileQueryMode.FullScan,
            CancellationToken.None);
        await secondStackReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        releaseFirstStackRead.TrySetResult();
        await Task.WhenAll(firstQuery, secondQuery).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(2, Volatile.Read(ref stackReadCount));
    }

    [TestMethod]
    public async Task GetExecutionProfileAsync_IncrementalCompletedProfilesEvictLeastRecentlyUsedEntryAfter64Results()
    {
        var stackReadCount = 0;
        var sampler = new ControlledEventPipeExecutionSampler();
        await using var fixture = CreateFixture(
            sampler,
            beforeStackFramesReadAsync: () =>
            {
                Interlocked.Increment(ref stackReadCount);
                return Task.CompletedTask;
            });
        await fixture.Session.StartAsync(fixture.Target, CancellationToken.None);
        await AppendSamplesAsync(fixture.Store, fixture.StartedAtUtc, Enumerable.Range(1, 66).ToArray());

        var ranges = Enumerable.Range(2, 65)
            .Select(second => new ExecutionTimeRange(fixture.StartedAtUtc, fixture.StartedAtUtc.AddSeconds(second)))
            .ToArray();
        for (var index = 0; index < 64; index++)
        {
            await fixture.Session.GetExecutionProfileAsync(
                ranges[index],
                ExecutionProfileQueryMode.Incremental,
                CancellationToken.None);
        }

        await fixture.Session.GetExecutionProfileAsync(
            ranges[0],
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None);
        await fixture.Session.GetExecutionProfileAsync(
            ranges[64],
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None);
        await fixture.Session.GetExecutionProfileAsync(
            ranges[1],
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None);
        await fixture.Session.GetExecutionProfileAsync(
            ranges[0],
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None);

        Assert.AreEqual(66, Volatile.Read(ref stackReadCount));
    }

    [TestMethod]
    public async Task GetExecutionProfileAsync_WhenCompletedCacheIsFull_DoesNotEvictInProgressSharedBuild()
    {
        var blockedBuildEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlockedBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stackReadCount = 0;
        async Task BlockSixtyFifthStackReadAsync()
        {
            if (Interlocked.Increment(ref stackReadCount) == 65)
            {
                blockedBuildEntered.TrySetResult();
                await releaseBlockedBuild.Task;
            }
        }

        var sampler = new ControlledEventPipeExecutionSampler();
        await using var fixture = CreateFixture(sampler, beforeStackFramesReadAsync: BlockSixtyFifthStackReadAsync);
        await fixture.Session.StartAsync(fixture.Target, CancellationToken.None);
        await AppendSamplesAsync(fixture.Store, fixture.StartedAtUtc, Enumerable.Range(1, 67).ToArray());
        var ranges = Enumerable.Range(2, 66)
            .Select(second => new ExecutionTimeRange(fixture.StartedAtUtc, fixture.StartedAtUtc.AddSeconds(second)))
            .ToArray();
        for (var index = 0; index < 64; index++)
        {
            await fixture.Session.GetExecutionProfileAsync(
                ranges[index],
                ExecutionProfileQueryMode.Incremental,
                CancellationToken.None);
        }

        var inProgressFirstWaiter = fixture.Session.GetExecutionProfileAsync(
            ranges[64],
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None);
        await blockedBuildEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var inProgressSecondWaiter = fixture.Session.GetExecutionProfileAsync(
            ranges[64],
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None);
        await fixture.Session.GetExecutionProfileAsync(
            ranges[65],
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None);
        await fixture.Session.GetExecutionProfileAsync(
            ranges[0],
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None);

        releaseBlockedBuild.TrySetResult();
        var firstProfile = await inProgressFirstWaiter.WaitAsync(TimeSpan.FromSeconds(1));
        var secondProfile = await inProgressSecondWaiter.WaitAsync(TimeSpan.FromSeconds(1));
        var cachedProfile = await fixture.Session.GetExecutionProfileAsync(
            ranges[64],
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None);

        Assert.AreSame(firstProfile, secondProfile);
        Assert.AreSame(firstProfile, cachedProfile);
        Assert.AreEqual(67, Volatile.Read(ref stackReadCount));
    }

    [TestMethod]
    public async Task GetExecutionProfileAsync_WhenMoreThan64BuildsAreQueued_PreservesEverySingleFlightEntry()
    {
        var firstTwoBuildsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unexpectedThirdBuildEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBuilds = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stackReadCount = 0;
        async Task BlockEveryStackReadAsync()
        {
            var currentCount = Interlocked.Increment(ref stackReadCount);
            if (currentCount == 2)
            {
                firstTwoBuildsEntered.TrySetResult();
            }
            else if (currentCount == 3)
            {
                unexpectedThirdBuildEntered.TrySetResult();
            }

            await releaseBuilds.Task;
        }

        var sampler = new ControlledEventPipeExecutionSampler();
        await using var fixture = CreateFixture(sampler, beforeStackFramesReadAsync: BlockEveryStackReadAsync);
        await fixture.Session.StartAsync(fixture.Target, CancellationToken.None);
        await AppendSamplesAsync(fixture.Store, fixture.StartedAtUtc, Enumerable.Range(1, 66).ToArray());
        var ranges = Enumerable.Range(2, 65)
            .Select(second => new ExecutionTimeRange(fixture.StartedAtUtc, fixture.StartedAtUtc.AddSeconds(second)))
            .ToArray();
        var firstWaiters = ranges
            .Select(range => fixture.Session.GetExecutionProfileAsync(
                range,
                ExecutionProfileQueryMode.Incremental,
                CancellationToken.None))
            .ToArray();
        await firstTwoBuildsEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var secondWaiters = ranges
            .Select(range => fixture.Session.GetExecutionProfileAsync(
                range,
                ExecutionProfileQueryMode.Incremental,
                CancellationToken.None))
            .ToArray();
        var thirdWaiterForFirstRange = fixture.Session.GetExecutionProfileAsync(
            ranges[0],
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None);

        try
        {
            var completedTask = await Task.WhenAny(
                unexpectedThirdBuildEntered.Task,
                Task.Delay(TimeSpan.FromMilliseconds(250)));

            Assert.AreNotSame(unexpectedThirdBuildEntered.Task, completedTask);
            Assert.AreEqual(2, Volatile.Read(ref stackReadCount));
        }
        finally
        {
            releaseBuilds.TrySetResult();
        }

        var firstProfiles = await Task.WhenAll(firstWaiters).WaitAsync(TimeSpan.FromSeconds(1));
        var secondProfiles = await Task.WhenAll(secondWaiters).WaitAsync(TimeSpan.FromSeconds(1));
        var thirdProfile = await thirdWaiterForFirstRange.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreSame(firstProfiles[0], secondProfiles[0]);
        Assert.AreSame(firstProfiles[0], thirdProfile);
        Assert.AreEqual(65, Volatile.Read(ref stackReadCount));
    }

    [TestMethod]
    public async Task GetExecutionProfileAsync_WhenIncrementalBuildFails_AllowsSameQueryToRebuild()
    {
        var stackReadCount = 0;
        Task FailFirstStackReadAsync()
        {
            return Interlocked.Increment(ref stackReadCount) == 1
                ? Task.FromException(new IOException("The first profile build failed."))
                : Task.CompletedTask;
        }

        var sampler = new ControlledEventPipeExecutionSampler();
        await using var fixture = CreateFixture(sampler, beforeStackFramesReadAsync: FailFirstStackReadAsync);
        await fixture.Session.StartAsync(fixture.Target, CancellationToken.None);
        await AppendSamplesAsync(fixture.Store, fixture.StartedAtUtc, 1, 2);
        var range = new ExecutionTimeRange(fixture.StartedAtUtc, fixture.StartedAtUtc.AddSeconds(2));

        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await fixture.Session.GetExecutionProfileAsync(
                range,
                ExecutionProfileQueryMode.Incremental,
                CancellationToken.None));

        var rebuiltProfile = await fixture.Session.GetExecutionProfileAsync(
            range,
            ExecutionProfileQueryMode.Incremental,
            CancellationToken.None);

        Assert.AreEqual(1, rebuiltProfile.ReceivedSampleCount);
        Assert.AreEqual(2, Volatile.Read(ref stackReadCount));
    }

    [TestMethod]
    public async Task GetExecutionProfileAsync_WhenOneQueryIsCancelled_DoesNotStopSamplingOrSharedSymbolLookup()
    {
        var sourcePath = CreateTemporarySourceFile();
        try
        {
            var sampler = new ControlledEventPipeExecutionSampler();
            var lookup = new ControlledSourceLocationLookup(sourcePath, waitForRelease: true);
            await using var fixture = CreateFixture(sampler, sourceLookup: lookup);
            await fixture.Session.StartAsync(fixture.Target, CancellationToken.None);
            await AppendSamplesAsync(fixture.Store, fixture.StartedAtUtc, 1, 2);
            using var cancellation = new CancellationTokenSource();

            var cancelledQuery = fixture.Session.GetExecutionProfileAsync(
                new ExecutionTimeRange(fixture.StartedAtUtc, fixture.StartedAtUtc.AddSeconds(2)),
                cancellation.Token);
            await lookup.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            cancellation.Cancel();

            try
            {
                await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelledQuery);
                await AppendSamplesAsync(fixture.Store, fixture.StartedAtUtc, 3);
                Assert.IsTrue(sampler.IsRunning);
                Assert.AreEqual(0, sampler.DisposeCallCount);
            }
            finally
            {
                lookup.Release.TrySetResult();
            }

            var survivingProfile = await fixture.Session.GetExecutionProfileAsync(
                new ExecutionTimeRange(fixture.StartedAtUtc, fixture.StartedAtUtc.AddSeconds(3)),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

            Assert.AreEqual(2, survivingProfile.ReceivedSampleCount);
            Assert.AreEqual(1, lookup.CallCount);
            Assert.IsNotNull(survivingProfile.Hotspots[0].Frame.SourceLocation);
        }
        finally
        {
            DeleteTemporarySourceFile(sourcePath);
        }
    }

    [TestMethod]
    public async Task GetExecutionProfileAsync_WhenRangeFallsOutsideSamplingWatermark_ThrowsStableRangeError()
    {
        var sampler = new ControlledEventPipeExecutionSampler();
        await using var fixture = CreateFixture(sampler);
        await fixture.Session.StartAsync(fixture.Target, CancellationToken.None);
        await AppendSamplesAsync(fixture.Store, fixture.StartedAtUtc, 1, 2);

        var beforeStart = await Assert.ThrowsExactlyAsync<DiagnosticsException>(async () =>
            await fixture.Session.GetExecutionProfileAsync(
                new ExecutionTimeRange(fixture.StartedAtUtc.AddTicks(-1), fixture.StartedAtUtc.AddSeconds(1)),
                CancellationToken.None));
        var afterWatermark = await Assert.ThrowsExactlyAsync<DiagnosticsException>(async () =>
            await fixture.Session.GetExecutionProfileAsync(
                new ExecutionTimeRange(fixture.StartedAtUtc, fixture.StartedAtUtc.AddSeconds(2).AddTicks(1)),
                CancellationToken.None));

        Assert.AreEqual(DiagnosticsErrorCode.ExecutionProfileRangeUnavailable, beforeStart.ErrorCode);
        Assert.AreEqual(DiagnosticsErrorCode.ExecutionProfileRangeUnavailable, afterWatermark.ErrorCode);
    }

    [TestMethod]
    public async Task DisposeAsync_CancelsRunningAndQueuedIncrementalBuildsThenReleasesResources()
    {
        var firstTwoBuildsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredBuildCount = 0;
        Task BlockUntilSessionEndsAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref enteredBuildCount) == 2)
            {
                firstTwoBuildsEntered.TrySetResult();
            }

            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        var sampler = new ControlledEventPipeExecutionSampler();
        await using var fixture = CreateFixture(
            sampler,
            beforeStackFramesReadWithCancellationAsync: BlockUntilSessionEndsAsync);
        await fixture.Session.StartAsync(fixture.Target, CancellationToken.None);
        await AppendSamplesAsync(fixture.Store, fixture.StartedAtUtc, 1, 2, 3, 4);
        var ranges = new[]
        {
            new ExecutionTimeRange(fixture.StartedAtUtc, fixture.StartedAtUtc.AddSeconds(2)),
            new ExecutionTimeRange(fixture.StartedAtUtc, fixture.StartedAtUtc.AddSeconds(3)),
            new ExecutionTimeRange(fixture.StartedAtUtc, fixture.StartedAtUtc.AddSeconds(4))
        };
        var queries = ranges
            .Select(range => fixture.Session.GetExecutionProfileAsync(
                range,
                ExecutionProfileQueryMode.Incremental,
                CancellationToken.None))
            .ToArray();
        await firstTwoBuildsEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var dispose = fixture.Session.DisposeAsync().AsTask();
        await sampler.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(1));
        foreach (var query in queries)
        {
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await query);
        }

        await dispose.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(2, Volatile.Read(ref enteredBuildCount));
        Assert.IsFalse(Directory.Exists(fixture.SessionDirectory));
        Assert.AreEqual(1, sampler.DisposeCallCount);
    }

    [TestMethod]
    public async Task DisposeAsync_StopsInputBeforeWaitingForQueryThenDeletesSessionStorage()
    {
        var sourcePath = CreateTemporarySourceFile();
        try
        {
            var sampler = new ControlledEventPipeExecutionSampler();
            var lookup = new ControlledSourceLocationLookup(sourcePath, waitForRelease: true);
            await using var fixture = CreateFixture(sampler, sourceLookup: lookup);
            await fixture.Session.StartAsync(fixture.Target, CancellationToken.None);
            await AppendSamplesAsync(fixture.Store, fixture.StartedAtUtc, 1, 2);
            var query = fixture.Session.GetExecutionProfileAsync(
                new ExecutionTimeRange(fixture.StartedAtUtc, fixture.StartedAtUtc.AddSeconds(2)),
                CancellationToken.None);
            await lookup.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var dispose = fixture.Session.DisposeAsync().AsTask();
            await sampler.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.IsFalse(dispose.IsCompleted);
            Assert.IsTrue(Directory.Exists(fixture.SessionDirectory));
            Assert.AreEqual(0, sampler.DisposeCallCount);
            var rejectedQuery = await Assert.ThrowsExactlyAsync<DiagnosticsException>(async () =>
                await fixture.Session.GetExecutionProfileAsync(
                    new ExecutionTimeRange(fixture.StartedAtUtc, fixture.StartedAtUtc.AddSeconds(2)),
                    CancellationToken.None));
            Assert.AreEqual(DiagnosticsErrorCode.ExecutionProfileRangeUnavailable, rejectedQuery.ErrorCode);

            lookup.Release.TrySetResult();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
                await query.WaitAsync(TimeSpan.FromSeconds(1)));
            await dispose.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.IsFalse(Directory.Exists(fixture.SessionDirectory));
            Assert.AreEqual(1, sampler.DisposeCallCount);
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
                await fixture.SymbolResolver.ResolveAsync(Frame(frameId: 101), CancellationToken.None));
        }
        finally
        {
            DeleteTemporarySourceFile(sourcePath);
        }
    }

    private static Exception CreateStartFailure(string failureKind) => failureKind switch
    {
        "diagnostics-client" => new DiagnosticsClientException("EventPipe unavailable."),
        "io" => new IOException("EventPipe transport failed."),
        "unauthorized" => new UnauthorizedAccessException("EventPipe access denied."),
        _ => throw new ArgumentOutOfRangeException(nameof(failureKind), failureKind, null)
    };

    private static async Task AppendSamplesAsync(
        ExecutionCaptureStore store,
        DateTimeOffset startedAtUtc,
        params int[] secondsAfterStart)
    {
        var frameId = await store.GetOrAddFrameAsync(
            new ExecutionFrameDescriptor("Worker.Run", "Worker", "C:\\Worker.dll", "worker-run"),
            CancellationToken.None);
        var stackId = await store.GetOrAddStackAsync(-1, frameId, CancellationToken.None);
        foreach (var seconds in secondsAfterStart)
        {
            await store.AppendAsync(
                new ExecutionSampleRecord(startedAtUtc.AddSeconds(seconds), ThreadId: 7, stackId),
                CancellationToken.None);
        }
    }

    private static void AssertCallTreeNodesEqual(
        ExecutionCallTreeNode expected,
        ExecutionCallTreeNode actual)
    {
        Assert.AreEqual(expected.Frame, actual.Frame);
        Assert.AreEqual(expected.InclusiveSampleCount, actual.InclusiveSampleCount);
        Assert.AreEqual(expected.ExclusiveSampleCount, actual.ExclusiveSampleCount);
        Assert.HasCount(expected.Children.Count, actual.Children);
        for (var index = 0; index < expected.Children.Count; index++)
        {
            AssertCallTreeNodesEqual(expected.Children[index], actual.Children[index]);
        }
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximum);
            if (candidate <= current
                || Interlocked.CompareExchange(ref maximum, candidate, current) == current)
            {
                return;
            }
        }
    }

    private static ExecutionFrameReference Frame(int frameId) => new(
        frameId,
        new ExecutionFrameDescriptor("Worker.Run", "Worker", "C:\\Worker.dll", $"symbol-{frameId}"),
        SymbolAddress: null);

    private static SessionFixture CreateFixture(
        ControlledEventPipeExecutionSampler sampler,
        Func<TimeSpan, CancellationToken, Task>? retryDelayAsync = null,
        ControlledSourceLocationLookup? sourceLookup = null,
        Func<Task>? beforeStackFramesReadAsync = null,
        Func<CancellationToken, Task>? beforeStackFramesReadWithCancellationAsync = null,
        int segmentDataLimitBytes = 4 * 1024 * 1024)
    {
        var root = Path.Combine(Path.GetTempPath(), "DotnetAnalysis.Tests", Guid.NewGuid().ToString("N"));
        var layout = new ExecutionCaptureStorageLayout(root);
        var store = new ExecutionCaptureStore(
            layout,
            segmentDataLimitBytes,
            beforeStackFramesReadAsync: beforeStackFramesReadAsync,
            beforeStackFramesReadWithCancellationAsync: beforeStackFramesReadWithCancellationAsync);
        var startedAtUtc = store.CaptureReadBoundary().StartedAtUtc;
        var symbolResolver = new ExecutionSymbolResolver(
            sourceLookup ?? new ControlledSourceLocationLookup(
                new ExecutionSourceLocationLookupResult(
                    ExecutionSourceLocationLookupStatus.DynamicModule,
                    null,
                    LineNumber: 0,
                    ColumnNumber: 0)));
        var session = new ExecutionSamplingSession(
            store,
            sampler,
            symbolResolver,
            new FixedTimeProvider(startedAtUtc),
            retryDelayAsync);
        var target = new TargetProcess(4567, startedAtUtc.AddMinutes(-1), "target", "C:\\target.exe");
        return new SessionFixture(root, layout.SessionDirectory, startedAtUtc, target, store, symbolResolver, session);
    }

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

    private sealed class ControlledEventPipeExecutionSampler : IEventPipeExecutionSampler
    {
        private readonly Queue<Exception> _startFailures;

        public ControlledEventPipeExecutionSampler(IEnumerable<Exception>? startFailures = null)
        {
            _startFailures = new Queue<Exception>(startFailures ?? []);
        }

        public TaskCompletionSource Stopped { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StartCallCount { get; private set; }

        public int DisposeCallCount { get; private set; }

        public int StopCallCount { get; private set; }

        public bool IsRunning { get; private set; }

        public long SuccessfulSampleCount { get; set; }

        public long LostEventCount { get; set; }

        public DiagnosticsException? TerminalFailure { get; set; }

        public Task StartAsync(TargetProcess target, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(target);
            cancellationToken.ThrowIfCancellationRequested();
            StartCallCount++;
            if (_startFailures.TryDequeue(out var failure))
            {
                throw failure;
            }

            IsRunning = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            IsRunning = false;
            return ValueTask.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCallCount++;
            IsRunning = false;
            Stopped.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class ControlledSourceLocationLookup : IExecutionSourceLocationLookup
    {
        private readonly ExecutionSourceLocationLookupResult _result;
        private readonly bool _waitForRelease;
        private int _callCount;

        public ControlledSourceLocationLookup(string sourcePath, bool waitForRelease)
            : this(
                new ExecutionSourceLocationLookupResult(
                    ExecutionSourceLocationLookupStatus.Found,
                    sourcePath,
                    LineNumber: 27,
                    ColumnNumber: 3),
                waitForRelease)
        {
        }

        public ControlledSourceLocationLookup(
            ExecutionSourceLocationLookupResult result,
            bool waitForRelease = false)
        {
            _result = result;
            _waitForRelease = waitForRelease;
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

            return _result;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class SessionFixture : IAsyncDisposable
    {
        private readonly string _root;

        public SessionFixture(
            string root,
            string sessionDirectory,
            DateTimeOffset startedAtUtc,
            TargetProcess target,
            ExecutionCaptureStore store,
            ExecutionSymbolResolver symbolResolver,
            ExecutionSamplingSession session)
        {
            _root = root;
            SessionDirectory = sessionDirectory;
            StartedAtUtc = startedAtUtc;
            Target = target;
            Store = store;
            SymbolResolver = symbolResolver;
            Session = session;
        }

        public string SessionDirectory { get; }

        public DateTimeOffset StartedAtUtc { get; }

        public TargetProcess Target { get; }

        public ExecutionCaptureStore Store { get; }

        public ExecutionSymbolResolver SymbolResolver { get; }

        public ExecutionSamplingSession Session { get; }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
