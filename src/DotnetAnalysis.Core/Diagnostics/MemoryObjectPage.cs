namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示指定类型对象的一页查询结果。
/// </summary>
public sealed record MemoryObjectPage
{
    /// <summary>
    /// 创建对象分页查询结果。
    /// </summary>
    /// <param name="objects">当前页中的对象。</param>
    /// <param name="totalObjectCount">该类型在快照中的对象总数。</param>
    /// <param name="offset">当前页相对于该类型对象列表的零基偏移量。</param>
    /// <param name="pageSize">请求的页大小，范围为 1 至 1000。</param>
    /// <exception cref="ArgumentOutOfRangeException">总数、偏移量或页大小不在有效范围时引发。</exception>
    /// <exception cref="ArgumentException">当前页对象数超过页大小时引发。</exception>
    public MemoryObjectPage(
        IReadOnlyList<MemoryObjectInfo> objects,
        long totalObjectCount,
        int offset,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (totalObjectCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalObjectCount), totalObjectCount, "Object count cannot be negative.");
        }

        if (offset < 0 || offset > totalObjectCount)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset must be within the object result range.");
        }

        if (pageSize is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "Page size must be between 1 and 1000.");
        }

        if (objects.Count > pageSize)
        {
            throw new ArgumentException("A page cannot contain more objects than its requested page size.", nameof(objects));
        }

        Objects = objects.ToArray();
        TotalObjectCount = totalObjectCount;
        Offset = offset;
        PageSize = pageSize;
    }

    /// <summary>
    /// 当前页中的对象。
    /// </summary>
    public IReadOnlyList<MemoryObjectInfo> Objects { get; }

    /// <summary>
    /// 该类型在快照中的对象总数。
    /// </summary>
    public long TotalObjectCount { get; }

    /// <summary>
    /// 当前页相对于该类型对象列表的零基偏移量。
    /// </summary>
    public int Offset { get; }

    /// <summary>
    /// 请求的页大小。
    /// </summary>
    public int PageSize { get; }

    /// <summary>
    /// 指示当前页之后是否仍有对象可读取。
    /// </summary>
    public bool HasNextPage => Offset < TotalObjectCount - Objects.Count;
}
