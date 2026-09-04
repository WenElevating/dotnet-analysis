namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 描述快照中的一个托管对象实例。
/// </summary>
public sealed record MemoryObjectInfo
{
    /// <summary>
    /// 创建对象描述。
    /// </summary>
    /// <param name="address">对象在快照中的地址。</param>
    /// <param name="type">对象类型。</param>
    /// <param name="sizeBytes">对象占用的字节数。</param>
    public MemoryObjectInfo(ulong address, TypeIdentity type, long sizeBytes)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (sizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), sizeBytes, "Object size cannot be negative.");
        }

        Address = address;
        Type = type;
        SizeBytes = sizeBytes;
    }

    /// <summary>
    /// 对象地址。
    /// </summary>
    public ulong Address { get; }

    /// <summary>
    /// 对象类型。
    /// </summary>
    public TypeIdentity Type { get; }

    /// <summary>
    /// 对象大小（字节）。
    /// </summary>
    public long SizeBytes { get; }
}
