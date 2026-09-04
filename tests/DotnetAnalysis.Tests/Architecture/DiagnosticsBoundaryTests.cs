using System.Xml.Linq;
using System.Diagnostics.CodeAnalysis;

namespace DotnetAnalysis.Tests.Architecture;

[TestClass]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names describe the required project boundaries.")]
public sealed class DiagnosticsBoundaryTests
{
    [TestMethod]
    public void Application_ContainsOnlyDiagnosticsContractsAndSharedEvents()
    {
        var sourceRoot = GetSourceRoot();
        var applicationRoot = Path.Combine(sourceRoot, "DotnetAnalysis.Application");

        Assert.IsFalse(File.Exists(Path.Combine(applicationRoot, "Sessions", "AttachedProcessSession.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(applicationRoot, "Snapshots", "MemorySnapshotOperation.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(applicationRoot, "Snapshots", "MemorySnapshotAnalysisService.cs")));

        var forbiddenTerms = new[]
        {
            "EventPipe",
            "GCDump",
            "SnapshotReader",
            "ProcessMemorySampler",
            "DotnetAnalysis.Diagnostics"
        };

        foreach (var source in Directory.EnumerateFiles(applicationRoot, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(source);
            foreach (var forbiddenTerm in forbiddenTerms)
            {
                Assert.IsFalse(
                    text.Contains(forbiddenTerm, StringComparison.Ordinal),
                    $"Application implementation must not contain '{forbiddenTerm}' ({source}).");
            }
        }
    }

    [TestMethod]
    public void ProjectReferences_FlowFromDiagnosticsToApplicationButNeverInReverse()
    {
        var sourceRoot = GetSourceRoot();
        var applicationProject = XDocument.Load(Path.Combine(
            sourceRoot,
            "DotnetAnalysis.Application",
            "DotnetAnalysis.Application.csproj"));
        var diagnosticsProject = XDocument.Load(Path.Combine(
            sourceRoot,
            "DotnetAnalysis.Diagnostics",
            "DotnetAnalysis.Diagnostics.csproj"));

        var applicationReferences = applicationProject
            .Descendants("ProjectReference")
            .Select(reference => (string?)reference.Attribute("Include"))
            .Where(include => include is not null)
            .ToArray();
        var diagnosticsReferences = diagnosticsProject
            .Descendants("ProjectReference")
            .Select(reference => (string?)reference.Attribute("Include"))
            .Where(include => include is not null)
            .ToArray();

        Assert.IsFalse(applicationReferences.Any(include => include!.Contains("DotnetAnalysis.Diagnostics", StringComparison.Ordinal)));
        Assert.IsTrue(diagnosticsReferences.Any(include => include!.Contains("DotnetAnalysis.Application", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AttachedProcessSession_DoesNotExistInProductionSources()
    {
        var sourceRoot = GetSourceRoot();
        var matches = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(source => Path.GetFileName(source).Equals("AttachedProcessSession.cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), matches);
    }

    private static string GetSourceRoot() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src"));
}
