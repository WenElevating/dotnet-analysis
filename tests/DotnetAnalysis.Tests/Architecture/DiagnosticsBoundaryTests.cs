using System.Xml.Linq;
using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

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
            "DotnetAnalysis.Diagnostics",
            "Microsoft.Diagnostics.NETCore.Client",
            "Microsoft.Diagnostics.Tracing",
            "Microsoft.Diagnostics.Symbols"
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
    public void Application_DoesNotReferenceExecutionProfilingInfrastructurePackages()
    {
        var sourceRoot = GetSourceRoot();
        var applicationProject = XDocument.Load(Path.Combine(
            sourceRoot,
            "DotnetAnalysis.Application",
            "DotnetAnalysis.Application.csproj"));
        var packageReferences = applicationProject
            .Descendants("PackageReference")
            .Select(reference => (string?)reference.Attribute("Include"))
            .Where(include => include is not null)
            .ToArray();

        var forbiddenPackages = new[]
        {
            "Microsoft.Diagnostics.NETCore.Client",
            "Microsoft.Diagnostics.Tracing",
            "Microsoft.Diagnostics.Symbols"
        };

        foreach (var forbiddenPackage in forbiddenPackages)
        {
            Assert.IsFalse(
                packageReferences.Any(package => package!.Equals(forbiddenPackage, StringComparison.Ordinal)),
                $"Application must not reference '{forbiddenPackage}'.");
        }
    }

    [TestMethod]
    public void ProcessDiagnosticsSession_ExposesExecutionProfileQueryContract()
    {
        var method = typeof(IProcessDiagnosticsSession).GetMethod(
            "GetExecutionProfileAsync",
            [typeof(ExecutionTimeRange), typeof(CancellationToken)]);

        Assert.IsNotNull(method);
        Assert.AreEqual(typeof(Task<ExecutionProfile>), method.ReturnType);
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

    [TestMethod]
    public void DiagnosticsCaptureResponsibilities_AreSeparatedIntoTheCaptureLayer()
    {
        var sourceRoot = GetSourceRoot();
        var diagnosticsRoot = Path.Combine(sourceRoot, "DotnetAnalysis.Diagnostics", "Windows");
        var sessionSource = File.ReadAllText(Path.Combine(diagnosticsRoot, "ProcessDiagnosticsSession.cs"));
        var diagnosticsSource = File.ReadAllText(Path.Combine(diagnosticsRoot, "WindowsProcessDiagnostics.cs"));
        var captureRoot = Path.Combine(diagnosticsRoot, "Capture");
        var captureSourcePath = Path.Combine(captureRoot, "GCDumpMemorySnapshotCapture.cs");

        Assert.IsFalse(sessionSource.Contains(
            "Func<CancellationToken, Task<MemorySnapshot>>",
            StringComparison.Ordinal));
        Assert.IsFalse(sessionSource.Contains("IAsyncDisposable ownedResource", StringComparison.Ordinal));
        Assert.IsFalse(sessionSource.Contains("_ownedResource", StringComparison.Ordinal));
        StringAssert.Contains(sessionSource, "IMemorySnapshotCapture");

        Assert.IsFalse(diagnosticsSource.Contains("CaptureSnapshotCoreAsync", StringComparison.Ordinal));
        Assert.IsFalse(diagnosticsSource.Contains("GCDumpSnapshotCollector", StringComparison.Ordinal));
        Assert.IsFalse(diagnosticsSource.Contains("PromoteAsync", StringComparison.Ordinal));
        StringAssert.Contains(diagnosticsSource, "IMemorySnapshotCapture _snapshotCapture");

        Assert.IsTrue(File.Exists(Path.Combine(captureRoot, "IMemorySnapshotCapture.cs")));
        Assert.IsTrue(File.Exists(captureSourcePath));

        var captureSource = File.ReadAllText(captureSourcePath);
        StringAssert.Contains(captureSource, "GCDumpSnapshotCollector.CaptureAsync");
        StringAssert.Contains(captureSource, "_snapshotStore.PromoteAsync");
        StringAssert.Contains(captureSource, "allocationCollector.Seal");
        StringAssert.Contains(captureSource, "allocationCollector.BeginNextInterval");
    }

    private static string GetSourceRoot() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src"));
}
