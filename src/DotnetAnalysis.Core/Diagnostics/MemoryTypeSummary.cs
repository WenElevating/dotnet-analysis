namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示快照中某一托管类型的对象数量和总大小。
/// </summary>
public sealed record MemoryTypeSummary
{
    /// <summary>
    /// 创建类型统计。
    /// </summary>
    /// <param name="type">统计对应的类型。</param>
    /// <param name="objectCount">对象数量。</param>
    /// <param name="totalSizeBytes">对象总大小（字节）。</param>
    public MemoryTypeSummary(TypeIdentity type, long objectCount, long totalSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (objectCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(objectCount), objectCount, "Object count cannot be negative.");
        }

        if (totalSizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalSizeBytes), totalSizeBytes, "Total size cannot be negative.");
        }

        Type = type;
        ObjectCount = objectCount;
        TotalSizeBytes = totalSizeBytes;
    }

    /// <summary>
    /// 统计对应的类型。
    /// </summary>
    public TypeIdentity Type { get; }

    /// <summary>
    /// 对象数量。
    /// </summary>
    public long ObjectCount { get; }

    /// <summary>
    /// 该类型对象总大小（字节）。
    /// </summary>
    public long TotalSizeBytes { get; }
}
