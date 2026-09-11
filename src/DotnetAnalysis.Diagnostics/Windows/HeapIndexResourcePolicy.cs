namespace DotnetAnalysis.Diagnostics.Windows;

/// <summary>
/// 集中计算大快照索引的内存预算、外排排序块和映射窗口限制，
/// 确保不同读取器不会各自采用互相冲突的资源上限。
/// </summary>
internal static class HeapIndexResourcePolicy
{
    private const long DominatorFixedOverheadBytes = 32L * 1024 * 1024;
    private const long DominatorGraphWindowBytes = 5 * DefaultMappedReadWindowBytes;

    /// <summary>低内存机器上仍允许后台索引逐步推进的最小预算。</summary>
    public const long MinimumBudgetBytes = 256L * 1024 * 1024;

    /// <summary>诊断进程索引工作允许使用的最大预算。</summary>
    public const long MaximumBudgetBytes = 768L * 1024 * 1024;

    /// <summary>外排排序块的最小大小。</summary>
    public const int MinimumExternalSortChunkBytes = 16 * 1024 * 1024;

    /// <summary>外排排序块的最大大小。</summary>
    public const int MaximumExternalSortChunkBytes = 64 * 1024 * 1024;

    /// <summary>所有长生命周期索引映射窗口合计的最大大小。</summary>
    public const long MaximumMappedWindowBytes = 64L * 1024 * 1024;

    /// <summary>单个随机读取器的默认映射窗口大小。</summary>
    public const long DefaultMappedReadWindowBytes = 8L * 1024 * 1024;

    /// <summary>
    /// 基于可用物理内存计算索引工作预算；结果为可用内存八分之一并夹在 256–768 MiB 之间。
    /// </summary>
    /// <param name="availableMemoryBytes">运行时报告的可用物理内存；未知时为零或负值。</param>
    /// <returns>可供单个索引工作使用的字节预算。</returns>
    public static long CalculateBudgetBytes(long availableMemoryBytes)
    {
        var oneEighth = availableMemoryBytes > 0 ? availableMemoryBytes / 8 : MinimumBudgetBytes;
        return Math.Min(MaximumBudgetBytes, Math.Max(MinimumBudgetBytes, oneEighth));
    }

    /// <summary>
    /// 根据统一预算选择外排排序块大小；预算的八分之一使低内存机器保持 32 MiB，
    /// 高内存机器逐步增大且绝不超过 64 MiB。
    /// </summary>
    /// <param name="availableMemoryBytes">运行时报告的可用物理内存；未知时为零或负值。</param>
    /// <returns>位于 16–64 MiB 范围内的排序块大小。</returns>
    public static int CalculateExternalSortChunkBytes(long availableMemoryBytes) =>
        (int)Math.Clamp(
            CalculateBudgetBytes(availableMemoryBytes) / 8,
            MinimumExternalSortChunkBytes,
            MaximumExternalSortChunkBytes);

    /// <summary>
    /// 读取当前运行时可用内存并返回索引预算。
    /// </summary>
    public static long GetBudgetBytes() => CalculateBudgetBytes(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    /// <summary>
    /// 读取当前运行时可用内存并返回外排排序块大小。
    /// </summary>
    public static int GetExternalSortChunkBytes() => CalculateExternalSortChunkBytes(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    /// <summary>
    /// 在支配树工作区、图窗口和固定开销之后，从剩余预算选择 16–64 MiB 的实际外排块；
    /// 首选块不适配时逐步缩小，只有连最小块都无法容纳时返回零。
    /// </summary>
    /// <param name="objectCount">基础索引对象数。</param>
    /// <param name="budgetBytes">当前派生构建可用预算。</param>
    /// <param name="preferredChunkBytes">常规资源策略给出的首选外排块大小。</param>
    /// <returns>可用的实际外排块字节数；资源不足以容纳最小块时为零。</returns>
    public static int CalculateDominatorExternalSortChunkBytes(
        int objectCount,
        long budgetBytes,
        int preferredChunkBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(objectCount);
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(preferredChunkBytes, 1);
        var boundedPreferredChunkBytes = Math.Clamp(
            preferredChunkBytes,
            MinimumExternalSortChunkBytes,
            MaximumExternalSortChunkBytes);

        var fixedRequiredBytes = checked(
            HeapDominatorWorkspace.CalculateLengthBytes(objectCount)
            + DominatorGraphWindowBytes
            + DominatorFixedOverheadBytes);
        if (fixedRequiredBytes > budgetBytes
            || budgetBytes - fixedRequiredBytes < MinimumExternalSortChunkBytes)
        {
            return 0;
        }

        return checked((int)Math.Min(boundedPreferredChunkBytes, budgetBytes - fixedRequiredBytes));
    }

    /// <summary>
    /// 判断文件映射 LT 工作区、基础图读取窗口和一个外排块能否同时落在指定构建预算内。
    /// 此门禁在创建大文件或映射前执行，资源不足时由上层返回稳定的派生分析不可用错误。
    /// </summary>
    /// <param name="objectCount">基础索引对象数。</param>
    /// <param name="budgetBytes">当前派生构建可用预算。</param>
    /// <param name="externalSortChunkBytes">Dominator 排序阶段的单块托管内存上限。</param>
    /// <returns>估算峰值不超过预算时返回 <see langword="true"/>。</returns>
    public static bool CanBuildDominator(int objectCount, long budgetBytes, int externalSortChunkBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(objectCount);
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(externalSortChunkBytes, 1);
        return CalculateDominatorExternalSortChunkBytes(
            objectCount,
            budgetBytes,
            externalSortChunkBytes) > 0;
    }
}
