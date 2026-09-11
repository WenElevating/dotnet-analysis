namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示按保留大小降序读取的支配树对象分页结果。
/// </summary>
public sealed record MemoryDominatorPage
{
    /// <summary>
    /// 创建一个支配树分页结果。
    /// </summary>
    /// <param name="objects">当前页中的对象。</param>
    /// <param name="totalObjectCount">所有可从非弱 GC 根到达的对象数量。</param>
    /// <param name="offset">本页的零基偏移量。</param>
    /// <param name="pageSize">请求的页大小，范围为 1 至 1000。</param>
    public MemoryDominatorPage(
        IReadOnlyList<MemoryDominatorInfo> objects,
        long totalObjectCount,
        int offset,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentOutOfRangeException.ThrowIfNegative(totalObjectCount);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 1000);
        if (offset > totalObjectCount)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset cannot exceed total object count.");
        }

        Objects = objects.ToArray();
        TotalObjectCount = totalObjectCount;
        Offset = offset;
        PageSize = pageSize;
    }

    /// <summary>
    /// 当前页对象。
    /// </summary>
    public IReadOnlyList<MemoryDominatorInfo> Objects { get; }

    /// <summary>
    /// 所有可分析对象的数量。
    /// </summary>
    public long TotalObjectCount { get; }

    /// <summary>
    /// 当前页起始偏移量。
    /// </summary>
    public int Offset { get; }

    /// <summary>
    /// 请求页大小。
    /// </summary>
    public int PageSize { get; }

    /// <summary>
    /// 是否仍有下一页。
    /// </summary>
    public bool HasNextPage => (long)Offset + Objects.Count < TotalObjectCount;
}
