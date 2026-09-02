using System.Diagnostics.CodeAnalysis;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class RuntimePrerequisitesTests
{
    [TestMethod]
    [TestCategory("WindowsDiagnosticsIntegration")]
    public void RequiredRuntimes_AreInstalledForNet8Net9AndNet10()
    {
        Assert.IsTrue(OperatingSystem.IsWindows(), "Windows is required for this category.");
        var output = string.Join("\n", System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", "--list-runtimes")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!.StandardOutput.ReadToEnd());
        StringAssert.Contains(output, "Microsoft.NETCore.App 8.");
        StringAssert.Contains(output, "Microsoft.NETCore.App 9.");
        StringAssert.Contains(output, "Microsoft.NETCore.App 10.");
    }
}
