using System.Diagnostics.CodeAnalysis;

namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示 CLR 为 GC 根报告的附加标志。
/// </summary>
[Flags]
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Flags 后缀明确表达 CLR 根位标记的公开契约。")]
public enum MemoryRootFlags
{
    /// <summary>
    /// 没有可用的附加标志。
    /// </summary>
    None = 0,

    /// <summary>
    /// 该根来自托管线程栈。
    /// </summary>
    StackRoot = 1,

    /// <summary>
    /// 该根指向对象内部地址而非对象起始地址。
    /// </summary>
    Interior = 2,

    /// <summary>
    /// 该根对应固定对象。
    /// </summary>
    Pinned = 4,

    /// <summary>
    /// 该根为弱引用，只描述 CLR 观察到的根标志，不构成对象存活的保留证据。
    /// </summary>
    WeakReference = 16,

    /// <summary>
    /// 该根使用引用计数互操作语义。
    /// </summary>
    RefCounted = 8
}
