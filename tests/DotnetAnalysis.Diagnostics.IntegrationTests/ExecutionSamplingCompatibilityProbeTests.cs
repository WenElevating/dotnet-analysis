using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace DotnetAnalysis.Diagnostics.IntegrationTests;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class ExecutionSamplingCompatibilityProbeTests
{
    public static IEnumerable<object[]> SupportedTargetFrameworks() =>
        IntegrationTestHost.GetSupportedTargetFrameworks().Select(static framework => new object[] { framework });

    [TestMethod]
    [DynamicData(nameof(SupportedTargetFrameworks))]
    [TestCategory("WindowsDiagnosticsIntegration")]
    [DoNotParallelize]
    [Timeout(15_000, CooperativeCancellation = true)]
    public async Task EventPipeExecutionSampler_OnSupportedTarget_UsesProductionAdapterAndPersistsRootToLeafStack(
        string targetFramework)
    {
        await using var target = await IntegrationTestHost.StartTargetAsync(
            targetFramework,
            new IntegrationTargetOptions(EnableExecutionWorkload: true));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var result = await ExecutionSamplingCompatibilityProbe.CollectAsync(
            target.ProcessId,
            TimeSpan.FromSeconds(10),
            timeout.Token);

        Assert.IsTrue(
            result.UsedTraceEventExecutionSource,
            "The sampler must consume the production TraceEvent execution source adapter.");
        Assert.IsGreaterThan(0L, result.ReceivedSampleCount);
        Assert.IsTrue(
            result.ManagedMethodNames.Any(name => name.Contains("ExecutionSamplingWorkload", StringComparison.Ordinal)),
            "The Sample Profiler call stacks must contain the controlled execution workload.");
        var rootIndex = FindFrameIndex(
            result.RepresentativeManagedStack,
            "ExecutionSamplingWorkload.RunWorker(");
        var leafIndex = FindFrameIndex(
            result.RepresentativeManagedStack,
            "ExecutionSamplingWorkload.Path");
        Assert.IsGreaterThanOrEqualTo(0, rootIndex, "The representative stack must contain RunWorker.");
        Assert.IsGreaterThanOrEqualTo(0, leafIndex, "The representative stack must contain a Path method.");
        Assert.IsLessThan(leafIndex, rootIndex, "Managed frames must be persisted from root to leaf.");
        Assert.AreEqual(0L, result.EventsLost);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task CollectAsync_WithInvalidDuration_WritesExceptionEvidence()
    {
        var existingArtifacts = GetProbeArtifactPaths();

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            ExecutionSamplingCompatibilityProbe.CollectAsync(
                Environment.ProcessId,
                TimeSpan.Zero,
                CancellationToken.None));

        AssertNewExceptionEvidence(existingArtifacts, nameof(ArgumentOutOfRangeException));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task CollectAsync_WithPreCancelledToken_WritesExceptionEvidence()
    {
        var existingArtifacts = GetProbeArtifactPaths();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            ExecutionSamplingCompatibilityProbe.CollectAsync(
                Environment.ProcessId,
                TimeSpan.FromSeconds(1),
                cancellation.Token));

        AssertNewExceptionEvidence(existingArtifacts, nameof(OperationCanceledException));
    }

    private static HashSet<string> GetProbeArtifactPaths()
    {
        var artifactRoot = Path.Combine(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
            "TestResults");
        return Directory.Exists(artifactRoot)
            ? Directory.EnumerateFiles(artifactRoot, "compatibility-probe.json", SearchOption.AllDirectories)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
    }

    private static int FindFrameIndex(IReadOnlyList<string> frames, string methodFragment)
    {
        for (var index = 0; index < frames.Count; index++)
        {
            if (frames[index].Contains(methodFragment, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static void AssertNewExceptionEvidence(
        IReadOnlySet<string> existingArtifacts,
        string expectedExceptionTypeName)
    {
        var newArtifacts = GetProbeArtifactPaths().Except(existingArtifacts, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.HasCount(1, newArtifacts, "Each failed probe call must write exactly one new evidence artifact.");

        using var document = JsonDocument.Parse(File.ReadAllText(newArtifacts[0]));
        var exception = document.RootElement.GetProperty("exception").GetString();
        StringAssert.Contains(exception, expectedExceptionTypeName);
    }
}
