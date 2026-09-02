using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Desktop.Infrastructure;

namespace DotnetAnalysis.Tests.Desktop;

[TestClass]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names describe the behavior under test.")]
public sealed class AsyncRelayCommandTests
{
    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task AsyncRelayCommand_Cancel_CancelsCurrentExecutionAndRestoresState()
    {
        // Arrange
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });

        // Act
        command.Execute(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsTrue(command.IsRunning);
        Assert.IsTrue(command.CanCancel);

        command.Cancel();
        await command.ExecutionTask!;

        // Assert
        Assert.IsFalse(command.IsRunning);
        Assert.IsFalse(command.CanCancel);
    }

    [TestMethod]
    public async Task AsyncRelayCommand_WhenExecutionFails_SetsLastErrorAndRaisesFailureEvent()
    {
        // Arrange
        var expected = new InvalidOperationException("analysis failed");
        var failureRaised = false;
        var command = new AsyncRelayCommand(_ => Task.FromException(expected));
        command.ExecutionFailed += (_, _) => failureRaised = true;

        // Act
        command.Execute(null);
        await command.ExecutionTask!;

        // Assert
        Assert.AreSame(expected, command.LastError);
        Assert.IsTrue(failureRaised);
    }

    [TestMethod]
    public void AsyncRelayCommand_WhenExecutionCompletes_RaisesCompletionNotificationsOnCallerSynchronizationContext()
    {
        // Arrange
        var previousContext = SynchronizationContext.Current;
        using var callerContext = new PumpingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(callerContext);

        try
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SynchronizationContext? isRunningChangedContext = null;
            SynchronizationContext? canExecuteChangedContext = null;
            var command = new AsyncRelayCommand(_ => completion.Task);
            command.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(AsyncRelayCommand.IsRunning) && !command.IsRunning)
                {
                    isRunningChangedContext = SynchronizationContext.Current;
                }
            };
            command.CanExecuteChanged += (_, _) =>
            {
                if (!command.IsRunning)
                {
                    canExecuteChangedContext = SynchronizationContext.Current;
                }
            };

            // Act
            command.Execute(null);
            Task.Run(() => completion.TrySetResult()).GetAwaiter().GetResult();
            callerContext.PumpUntil(
                () => command.ExecutionTask!.IsCompleted,
                TimeSpan.FromSeconds(1));
            command.ExecutionTask!.GetAwaiter().GetResult();

            // Assert
            Assert.AreSame(callerContext, isRunningChangedContext);
            Assert.AreSame(callerContext, canExecuteChangedContext);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private sealed class PumpingSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _workItems = new();
        private readonly AutoResetEvent _workAvailable = new(initialState: false);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _workItems.Enqueue((callback, state));
            _workAvailable.Set();
        }

        public void PumpUntil(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!condition())
            {
                if (_workItems.TryDequeue(out var workItem))
                {
                    workItem.Callback(workItem.State);
                    continue;
                }

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || !_workAvailable.WaitOne(remaining))
                {
                    throw new TimeoutException("The synchronization-context continuation did not complete in time.");
                }
            }
        }

        public void Dispose() => _workAvailable.Dispose();
    }
}
