using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Desktop.Infrastructure;

namespace DotnetAnalysis.Tests.Desktop;

[TestClass]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names describe the behavior under test.")]
public sealed class RelayCommandTests
{
    [TestMethod]
    public void RelayCommand_UsesCanExecutePredicate()
    {
        // Arrange
        var enabled = false;
        var calls = 0;
        var command = new RelayCommand(() => calls++, () => enabled);

        // Act and assert
        Assert.IsFalse(command.CanExecute(null));
        enabled = true;
        command.NotifyCanExecuteChanged();
        Assert.IsTrue(command.CanExecute(null));

        command.Execute(null);

        Assert.AreEqual(1, calls);
    }
}
