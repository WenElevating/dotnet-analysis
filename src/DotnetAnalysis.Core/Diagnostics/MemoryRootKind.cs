namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示 CLR 在快照中报告的 GC 根类别。
/// </summary>
public enum MemoryRootKind
{
    /// <summary>
    /// 根类别未知；通常表示导入的 GCDump 未提供根证据。
    /// </summary>
    Unknown,

    /// <summary>
    /// 托管线程栈上的根。
    /// </summary>
    Stack,

    /// <summary>
    /// CLR 句柄表中的根。
    /// </summary>
    Handle,

    /// <summary>
    /// 终结器队列保留的根。
    /// </summary>
    Finalizer,

    /// <summary>
    /// CLR 报告但不能归入其他公开类别的根。
    /// </summary>
    Other
}
