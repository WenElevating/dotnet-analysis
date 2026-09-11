using System.Diagnostics.CodeAnalysis;
using DotnetAnalysis.Diagnostics.Windows;

namespace DotnetAnalysis.Tests.Diagnostics;

/// <summary>
/// 验证大快照索引的统一资源策略始终保留低内存下限，并在资源充足时扩大外排排序块而不突破上限。
/// </summary>
[TestClass]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "测试名称描述场景。")]
public sealed class HeapIndexResourcePolicyTests
{
    /// <summary>
    /// 可用内存未知或偏低时，索引预算必须保留 256 MiB，排序块不得低于 16 MiB。
    /// </summary>
    [TestMethod]
    public void Calculate_WhenAvailableMemoryIsUnknown_UsesMinimumBudgetAndChunk()
    {
        Assert.AreEqual(256L * 1024 * 1024, HeapIndexResourcePolicy.CalculateBudgetBytes(0));
        Assert.AreEqual(32 * 1024 * 1024, HeapIndexResourcePolicy.CalculateExternalSortChunkBytes(0));
    }

    /// <summary>
    /// 可用内存充足时，预算最多 768 MiB，外排块最多 64 MiB。
    /// </summary>
    [TestMethod]
    public void Calculate_WhenAvailableMemoryIsLarge_ClampsBudgetAndChunkAtProductionLimits()
    {
        Assert.AreEqual(768L * 1024 * 1024, HeapIndexResourcePolicy.CalculateBudgetBytes(64L * 1024 * 1024 * 1024));
        Assert.AreEqual(64 * 1024 * 1024, HeapIndexResourcePolicy.CalculateExternalSortChunkBytes(64L * 1024 * 1024 * 1024));
    }

    /// <summary>
    /// GCDump 与保留快照的外排排序必须复用同一资源策略，不能各自保留会随可用内存产生不同结果的旧公式。
    /// </summary>
    [TestMethod]
    public void Readers_DoNotKeepIndependentExternalSortChunkSizingFormula()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;

        Assert.IsNull(typeof(GCDumpSnapshotReader).GetMethod("GetExternalSortChunkBytes", flags));
        Assert.IsNull(typeof(RetentionHeapSnapshot).GetMethod("GetExternalSortChunkBytes", flags));
    }

    /// <summary>
    /// 10M 支配树在最大生产预算下必须可进入文件映射路径，而同一规模在最低预算下应被稳定拒绝；
    /// 资源门禁必须在分配工作区之前做出决定。
    /// </summary>
    [TestMethod]
    public void CanBuildDominator_WhenObjectCountIsTenMillion_UsesWorkspaceAndSortBudget()
    {
        Assert.IsTrue(HeapIndexResourcePolicy.CanBuildDominator(
            10_000_000,
            HeapIndexResourcePolicy.MaximumBudgetBytes,
            HeapIndexResourcePolicy.MaximumExternalSortChunkBytes));
        Assert.IsFalse(HeapIndexResourcePolicy.CanBuildDominator(
            10_000_000,
            HeapIndexResourcePolicy.MinimumBudgetBytes,
            HeapIndexResourcePolicy.MinimumExternalSortChunkBytes));
    }

    /// <summary>
    /// 首选 64 MiB 排序块超出剩余预算时，只要仍能容纳至少 16 MiB 就必须缩小块继续构建；
    /// 剩余空间连最小块都容不下时才返回零并拒绝派生分析。
    /// </summary>
    [TestMethod]
    public void CalculateDominatorExternalSortChunkBytes_WhenPreferredChunkDoesNotFit_UsesRemainingBudgetDownToMinimum()
    {
        var reducedChunkBytes = HeapIndexResourcePolicy.CalculateDominatorExternalSortChunkBytes(
            13_500_000,
            HeapIndexResourcePolicy.MaximumBudgetBytes,
            HeapIndexResourcePolicy.MaximumExternalSortChunkBytes);
        var refusedChunkBytes = HeapIndexResourcePolicy.CalculateDominatorExternalSortChunkBytes(
            13_750_000,
            HeapIndexResourcePolicy.MaximumBudgetBytes,
            HeapIndexResourcePolicy.MaximumExternalSortChunkBytes);

        Assert.IsGreaterThanOrEqualTo(HeapIndexResourcePolicy.MinimumExternalSortChunkBytes, reducedChunkBytes);
        Assert.IsLessThan(HeapIndexResourcePolicy.MaximumExternalSortChunkBytes, reducedChunkBytes);
        Assert.IsTrue(HeapIndexResourcePolicy.CanBuildDominator(
            13_500_000,
            HeapIndexResourcePolicy.MaximumBudgetBytes,
            HeapIndexResourcePolicy.MaximumExternalSortChunkBytes));
        Assert.AreEqual(0, refusedChunkBytes);
        Assert.IsFalse(HeapIndexResourcePolicy.CanBuildDominator(
            13_750_000,
            HeapIndexResourcePolicy.MaximumBudgetBytes,
            HeapIndexResourcePolicy.MaximumExternalSortChunkBytes));
    }
}
