using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Tests.Diagnostics;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class AllocationProfileBuilderTests
{
    [TestMethod]
    public void Seal_WhenFramesDifferByModuleOrLine_KeepsSeparateHotspots()
    {
        var builder = new AllocationProfileBuilder(DateTimeOffset.UnixEpoch);
        var type = new TypeIdentity("Sample.Node", "Sample");

        builder.Add(type, [new CallStackFrame("Allocate", "FirstModule", 10)], 16);
        builder.Add(type, [new CallStackFrame("Allocate", "SecondModule", 20)], 32);

        var profile = builder.Seal(DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.HasCount(2, profile.Hotspots);
        Assert.IsTrue(profile.Hotspots.Any(hotspot => hotspot.ObservedAllocatedBytes == 16L));
        Assert.IsTrue(profile.Hotspots.Any(hotspot => hotspot.ObservedAllocatedBytes == 32L));
    }
}
