using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Tests.Diagnostics;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class AllocationCallStackCacheTests
{
    [TestMethod]
    public void TryGetOrAdd_WhenCapacityIsReached_KeepsExistingStackAndRejectsNewStack()
    {
        var cache = new AllocationCallStackCache(1);
        var existing = new[] { new CallStackFrame("First", "Sample", null) };
        var newStack = new[] { new CallStackFrame("Second", "Sample", null) };

        Assert.IsTrue(cache.TryGetOrAdd(existing, out var cachedExisting));
        Assert.IsFalse(cache.TryGetOrAdd(newStack, out var rejected));
        Assert.IsTrue(cache.TryGetOrAdd(existing, out var cachedAgain));
        CollectionAssert.AreEqual(existing, cachedExisting.ToArray());
        Assert.IsEmpty(rejected);
        CollectionAssert.AreEqual(existing, cachedAgain.ToArray());
    }
}
