namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示不可为空的内存快照标识。
/// </summary>
public readonly record struct MemorySnapshotId
{
    /// <summary>
    /// 以现有 GUID 创建快照标识。
    /// </summary>
    /// <param name="value">非空 GUID 值。</param>
    public MemorySnapshotId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Snapshot ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    /// <summary>
    /// 底层 GUID 值。
    /// </summary>
    public Guid Value { get; }

    /// <summary>
    /// 生成新的快照标识。
    /// </summary>
    public static MemorySnapshotId New() => new(Guid.NewGuid());

    /// <summary>
    /// 以无连字符格式显示标识。
    /// </summary>
    public override string ToString() => Value.ToString("N");
}
