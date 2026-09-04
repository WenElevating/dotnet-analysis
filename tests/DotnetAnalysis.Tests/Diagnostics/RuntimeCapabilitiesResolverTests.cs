using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Tests.Diagnostics;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class RuntimeCapabilitiesResolverTests
{
    [TestMethod]
    public async Task ValidateAsync_WhenTargetIsNotAmd64_RejectsRuntime()
    {
        var resolver = new RuntimeCapabilitiesResolver(
            new SupportedRuntimeInspector(),
            new UnsupportedArchitectureInspector());

        var exception = await Assert.ThrowsAsync<DiagnosticsException>(
            async () => await resolver.ValidateAsync(Target(), CancellationToken.None));

        Assert.AreEqual(DiagnosticsErrorCode.RuntimeNotSupported, exception.ErrorCode);
    }

    private static TargetProcess Target() => new(
        42,
        DateTimeOffset.Parse("2026-09-04T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        "test",
        "C:\\test.exe");

    private sealed class SupportedRuntimeInspector : IProcessRuntimeInspector
    {
        public bool IsWindows => true;

        public bool Is64BitOperatingSystem => true;

        public bool IsCoreClr(TargetProcess process) => true;

        public int GetRuntimeMajorVersion(TargetProcess process) => 10;
    }

    private sealed class UnsupportedArchitectureInspector : IProcessArchitectureInspector
    {
        public bool IsAmd64(TargetProcess process) => false;
    }
}
