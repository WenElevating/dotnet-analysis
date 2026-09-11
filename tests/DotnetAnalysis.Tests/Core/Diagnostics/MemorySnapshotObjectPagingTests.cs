using DotnetAnalysis.Core.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace DotnetAnalysis.Tests.Core.Diagnostics;

[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names describe behavior.")]
public sealed class MemorySnapshotObjectPagingTests
{
    [TestMethod]
    public void DetermineObjectAccessMode_UsesOneHundredThousandObjectBoundary()
    {
        Assert.AreEqual(
            MemorySnapshotObjectAccessMode.Full,
            MemorySnapshotAnalysis.DetermineObjectAccessMode(99_999));
        Assert.AreEqual(
            MemorySnapshotObjectAccessMode.Paged,
            MemorySnapshotAnalysis.DetermineObjectAccessMode(100_000));
    }

    [TestMethod]
    public void ObjectPage_ReportsRangeAndFollowingPage()
    {
        var type = new TypeIdentity("Sample.Type", "Sample");
        var page = new MemoryObjectPage(
            [new MemoryObjectInfo(1, type, 16), new MemoryObjectInfo(2, type, 16)],
            totalObjectCount: 5,
            offset: 2,
            pageSize: 2);

        Assert.AreEqual(5L, page.TotalObjectCount);
        Assert.AreEqual(2, page.Offset);
        Assert.AreEqual(2, page.PageSize);
        Assert.IsTrue(page.HasNextPage);
        Assert.HasCount(2, page.Objects);
    }

    [TestMethod]
    public void ObjectPage_RejectsInvalidRange()
    {
        var type = new TypeIdentity("Sample.Type", "Sample");

        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new MemoryObjectPage([], 0, -1, 1));
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new MemoryObjectPage([], 0, 0, 0));
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new MemoryObjectPage([], 0, 0, 1001));
    }

    /// <summary>
    /// 类型增长比较仅聚合对象数和浅表大小，不能混入会重叠的 retained size。
    /// </summary>
    [TestMethod]
    public void TypeGrowth_ReportsSignedObjectAndShallowSizeDeltas()
    {
        var type = new TypeIdentity("Sample.Type", "Sample");
        var growth = new MemoryTypeGrowth(type, baselineObjectCount: 2, candidateObjectCount: 5, baselineShallowSizeBytes: 32, candidateShallowSizeBytes: 80);

        Assert.AreEqual(3L, growth.ObjectCountGrowth);
        Assert.AreEqual(48L, growth.ShallowSizeGrowthBytes);
    }
}
