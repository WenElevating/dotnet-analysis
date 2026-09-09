namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 指示内存快照捕获所使用的证据级别和实现方式。
/// </summary>
public enum MemorySnapshotCaptureMode
{
    /// <summary>
    /// 使用现有轻量 GCDump 捕获；不承诺 GC 根类别或持有函数证据。
    /// </summary>
    Standard,

    /// <summary>
    /// 使用 CLR Profiler 捕获对象图、GC 根类别和可验证的栈根函数证据。
    /// </summary>
    RetentionAnalysis
}
