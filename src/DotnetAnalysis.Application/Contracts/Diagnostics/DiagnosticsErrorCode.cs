namespace DotnetAnalysis.Application.Contracts.Diagnostics;

/// <summary>
/// 诊断 API 对外稳定的错误分类。
/// </summary>
public enum DiagnosticsErrorCode
{
    /// <summary>
    /// 访问目标或诊断接口被拒绝。
    /// </summary>
    AccessDenied,
    /// <summary>
    /// 目标进程已退出。
    /// </summary>
    TargetExited,
    /// <summary>
    /// 目标 PID 已被其他进程复用。
    /// </summary>
    TargetChanged,
    /// <summary>
    /// 目标运行时或平台不受支持。
    /// </summary>
    RuntimeNotSupported,
    /// <summary>
    /// 快照文件格式不受当前适配器支持。
    /// </summary>
    SnapshotFormatNotSupported,
    /// <summary>
    /// 捕获、读取或持久化快照失败。
    /// </summary>
    CaptureFailed,
    /// <summary>
    /// 操作被调用方取消。
    /// </summary>
    CaptureCancelled,
    /// <summary>
    /// 快照对象数量过大，不能执行完整对象枚举，应改用分页查询。
    /// </summary>
    SnapshotTooLargeForFullEnumeration
}
