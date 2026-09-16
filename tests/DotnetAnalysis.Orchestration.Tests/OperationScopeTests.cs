using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Application.Events;
using DotnetAnalysis.Orchestration.Models;
using DotnetAnalysis.Orchestration.Operations;

namespace DotnetAnalysis.Orchestration.Tests;

/// <summary>验证操作取消、截止时间、代次隔离和幂等收尾。</summary>
[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names describe the required contract behavior.")]
public sealed class OperationScopeTests
{
    /// <summary>验证每个作用域都分配独立的操作身份。</summary>
    [TestMethod]
    public void Scope_CreatesUniqueOperationIdentities()
    {
        using var first = new OperationScope(Guid.NewGuid());
        using var second = new OperationScope(first.Operation.Generation);

        Assert.AreNotEqual(first.Operation.OperationId, second.Operation.OperationId);
        Assert.AreEqual(first.Operation.Generation, second.Operation.Generation);
    }

    [TestMethod]
    public async Task ExternalCancellation_OnlyCancelsCurrentOperation()
    {
        using var source = new CancellationTokenSource();
        await using var canceled = new OperationScope(Guid.NewGuid(), cancellationToken: source.Token);
        await using var unaffected = new OperationScope(canceled.Operation.Generation);

        source.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await Task.Delay(Timeout.InfiniteTimeSpan, canceled.CancellationToken));
        Assert.IsTrue(canceled.CancellationToken.IsCancellationRequested);
        Assert.IsFalse(unaffected.CancellationToken.IsCancellationRequested);
        Assert.AreEqual(DiagnosticOperationStatus.Canceled, canceled.Operation.Status);
    }

    [TestMethod]
    public void ClockDeadline_TransitionsToTimeout()
    {
        var time = new TestTimeProvider();
        using var scope = new OperationScope(Guid.NewGuid(), TimeSpan.FromSeconds(5), timeProvider: time);

        time.Advance(TimeSpan.FromSeconds(5));

        Assert.IsTrue(scope.CancellationToken.IsCancellationRequested);
        Assert.AreEqual(DiagnosticOperationStatus.TimedOut, scope.Operation.Status);
    }

    [TestMethod]
    public void AcceptsResult_RejectsMismatchedOrStaleIdentity()
    {
        var generation = Guid.NewGuid();
        var session = ProcessDiagnosticsSessionId.New();
        using var scope = new OperationScope(generation, session);

        Assert.IsTrue(scope.AcceptsResult(generation, session, scope.Operation.OperationId));
        Assert.IsFalse(scope.AcceptsResult(Guid.NewGuid(), session, scope.Operation.OperationId));
        Assert.IsFalse(scope.AcceptsResult(generation, ProcessDiagnosticsSessionId.New(), scope.Operation.OperationId));
        Assert.IsFalse(scope.AcceptsResult(generation, session, Guid.NewGuid()));

        scope.Dispose();
        Assert.IsFalse(scope.AcceptsResult(generation, session, scope.Operation.OperationId));
    }

    [TestMethod]
    public void OperationEvent_ExposesGenerationAndOperationIdentity()
    {
        var generation = Guid.NewGuid();
        var session = ProcessDiagnosticsSessionId.New();
        using var scope = new OperationScope(generation, session);
        var applicationEvent = new MemorySnapshotCaptureStarted(session, DateTimeOffset.UtcNow, "test")
        {
            Generation = generation,
            OperationId = scope.Operation.OperationId
        };

        Assert.IsTrue(scope.AcceptsResult(applicationEvent));
        Assert.IsTrue(applicationEvent.Matches(generation, session, scope.Operation.OperationId));
    }

    [TestMethod]
    public async Task CompletionAndDisposal_AreIdempotent()
    {
        await using var scope = new OperationScope(Guid.NewGuid());

        Assert.IsTrue(scope.Operation.TryStart());
        Assert.IsTrue(scope.Operation.TryComplete("stable"));
        Assert.IsFalse(scope.Operation.TryComplete("replacement"));
        Assert.AreEqual("stable", scope.Operation.Result);

        await scope.DisposeAsync();
        await scope.DisposeAsync();
        Assert.AreEqual(DiagnosticOperationStatus.Succeeded, scope.Operation.Status);
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
        private readonly List<TestTimer> _timers = [];

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new TestTimer(callback, state, _utcNow + dueTime);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            _utcNow += amount;
            foreach (var timer in _timers.Where(timer => !timer.IsDisposed && timer.DueAt <= _utcNow).ToArray()) timer.Fire();
        }

        private sealed class TestTimer(TimerCallback callback, object? state, DateTimeOffset dueAt) : ITimer
        {
            private readonly TimerCallback _callback = callback;
            private readonly object? _state = state;
            public DateTimeOffset DueAt { get; } = dueAt;
            public bool IsDisposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !IsDisposed;
            public void Dispose() => IsDisposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
            public void Fire() { if (!IsDisposed) _callback(_state); }
        }
    }
}
    /// <summary>验证外部取消只影响拥有该令牌的当前作用域。</summary>
    /// <summary>验证可控时钟推进会触发超时终态和取消令牌。</summary>
    /// <summary>验证代次、会话和操作身份任一不匹配都会拒绝结果。</summary>
    /// <summary>验证事件身份可被宿主检查且完成与释放操作幂等。</summary>
    /// <summary>验证成功结果不会被重复完成或释放覆盖。</summary>
