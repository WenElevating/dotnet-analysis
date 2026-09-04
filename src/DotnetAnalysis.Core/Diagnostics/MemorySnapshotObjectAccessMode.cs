namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示快照对象实例的读取方式。
/// </summary>
public enum MemorySnapshotObjectAccessMode
{
    /// <summary>
    /// 对象数量较小，可一次读取指定类型的全部对象。
    /// </summary>
    Full,

    /// <summary>
    /// 对象数量较大，只能按页读取指定类型的对象。
    /// </summary>
    Paged
}
