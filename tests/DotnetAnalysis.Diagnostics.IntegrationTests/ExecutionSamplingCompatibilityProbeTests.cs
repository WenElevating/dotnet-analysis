using System.Diagnostics.CodeAnalysis;

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
    public async Task SampleProfiler_OnSupportedTarget_ProducesManagedStackAndNoLostEvents(string targetFramework)
    {
        await using var target = await IntegrationTestHost.StartTargetAsync(
            targetFramework,
            new IntegrationTargetOptions(EnableExecutionWorkload: true));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var result = await ExecutionSamplingCompatibilityProbe.CollectAsync(
            target.ProcessId,
            TimeSpan.FromSeconds(10),
            timeout.Token);

        Assert.IsGreaterThan(0L, result.ReceivedSampleCount);
        Assert.IsTrue(
            result.ManagedMethodNames.Any(name => name.Contains("ExecutionSamplingWorkload", StringComparison.Ordinal)),
            "The Sample Profiler call stacks must contain the controlled execution workload.");
        Assert.AreEqual(0L, result.EventsLost);
    }
}
