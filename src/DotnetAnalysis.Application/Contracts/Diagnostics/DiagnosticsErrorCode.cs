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
    /// 无法向目标进程附加所需的 CLR Profiler，例如目标已附加其他 Profiler。
    /// </summary>
    ProfilerAttachUnavailable,
    /// <summary>
    /// CLR Profiler 已开始工作但未能生成有效的保留分析快照。
    /// </summary>
    ProfilerCaptureFailed,
    /// <summary>
    /// 保留分析快照达到单文件、总量或卷保留空间限制。
    /// </summary>
    SnapshotStorageLimitReached,
    /// <summary>
    /// 快照对象数量过大，不能执行完整对象枚举，应改用分页查询。
    /// </summary>
    SnapshotTooLargeForFullEnumeration,
    /// <summary>
    /// 构建基础堆索引失败，原始快照仍会被保留。
    /// </summary>
    SnapshotIndexBuildFailed,
    /// <summary>
    /// 查询超过当前快照索引的资源或访问上限。
    /// </summary>
    SnapshotQueryLimitReached,
    /// <summary>
    /// 支配树等派生分析无法构建或读取；基础索引查询仍可使用。
    /// </summary>
    DerivedAnalysisUnavailable,
    /// <summary>
    /// CLR Profiler 的保留捕获环形缓冲区耗尽，快照未被伪造为成功。
    /// </summary>
    ProfilerCaptureBufferExhausted,
    /// <summary>
    /// 目标运行时或当前会话不支持执行采样。
    /// </summary>
    ExecutionProfilingUnavailable,
    /// <summary>
    /// 请求的执行采样时间区间不可用。
    /// </summary>
    ExecutionProfileRangeUnavailable,
    /// <summary>
    /// 读取或持久化执行采样分析结果失败。
    /// </summary>
    ExecutionProfileStorageFailed
}
