using System.Diagnostics.CodeAnalysis;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class RuntimePrerequisitesTests
{
    private static readonly int[] s_requiredRuntimeMajors = [8, 10];

    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    public void RequiredRuntimes_AreInstalledForNet8AndNet10()
    {
        Assert.IsTrue(OperatingSystem.IsWindows(), "Windows is required for this category.");
        var installed = IntegrationTestHost.GetInstalledRuntimeMajorVersions();

        CollectionAssert.IsSubsetOf(s_requiredRuntimeMajors, installed.ToArray());
    }
}
