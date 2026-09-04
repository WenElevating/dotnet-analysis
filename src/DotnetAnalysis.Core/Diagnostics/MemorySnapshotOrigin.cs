namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 指示内存快照是实时捕获还是由文件导入。
/// </summary>
public enum MemorySnapshotOrigin
{
    /// <summary>
    /// 从已附着的目标进程实时捕获。
    /// </summary>
    Captured,
    /// <summary>
    /// 从现有快照文件导入。
    /// </summary>
    Imported
}
