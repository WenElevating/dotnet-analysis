using System.Diagnostics.CodeAnalysis;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class WindowsDiagnosticsIntegrationTests
{
    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    [DataRow("net8.0")]
    [DataRow("net9.0")]
    [DataRow("net10.0")]
    public async Task AttachedTarget_StartsAndCleansUp(string targetFramework)
    {
        await using var target = await IntegrationTestHost.StartTargetAsync(targetFramework);
        Assert.IsGreaterThan(0, target.ProcessId);
    }
}
