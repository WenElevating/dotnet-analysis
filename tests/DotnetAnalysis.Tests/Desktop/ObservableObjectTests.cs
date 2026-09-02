using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Desktop.Infrastructure;

namespace DotnetAnalysis.Tests.Desktop;

[TestClass]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names describe the behavior under test.")]
public sealed class ObservableObjectTests
{
    private static readonly string[] s_expectedPropertyNames = ["Name"];

    [TestMethod]
    public void SetProperty_WhenValueChanges_RaisesPropertyChangedOnce()
    {
        // Arrange
        var viewModel = new TestViewModel();
        var names = new List<string?>();
        viewModel.PropertyChanged += (_, args) => names.Add(args.PropertyName);

        // Act
        viewModel.Name = "heap";
        viewModel.Name = "heap";

        // Assert
        CollectionAssert.AreEqual(s_expectedPropertyNames, names);
    }

    private sealed class TestViewModel : ObservableObject
    {
        private string? _name;

        public string? Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }
    }
}
