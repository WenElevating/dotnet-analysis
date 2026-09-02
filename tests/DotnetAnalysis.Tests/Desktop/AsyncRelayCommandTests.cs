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
}
