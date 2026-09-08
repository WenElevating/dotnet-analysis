using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class IntegrationTestHostTests
{
    private static readonly int[] s_installedRuntimeMajorVersions = [7, 9, 11];
    private static readonly string[] s_expectedTargetFrameworks = ["net9.0"];

    [TestMethod]
    public void GetSupportedTargetFrameworks_OnlyReturnsInstalledCompatibleRuntimes()
    {
        var frameworks = IntegrationTestHost.GetSupportedTargetFrameworks(s_installedRuntimeMajorVersions).ToArray();

        CollectionAssert.AreEqual(s_expectedTargetFrameworks, frameworks);
    }

    [TestMethod]
    public void CreateTargetProcessStartInfo_DisablesInheritedExecutionWorkload()
    {
        var processStartInfo = IntegrationTestHost.CreateTargetProcessStartInfo(
            "test-target.exe",
            new IntegrationTargetOptions());

        Assert.AreEqual("false", processStartInfo.Environment["DOTNET_ANALYSIS_TEST_EXECUTION_WORKLOAD"]);
        Assert.IsFalse(processStartInfo.UseShellExecute);
        Assert.IsTrue(processStartInfo.RedirectStandardInput);
        Assert.IsTrue(processStartInfo.RedirectStandardOutput);
        Assert.IsTrue(processStartInfo.RedirectStandardError);
    }
}
