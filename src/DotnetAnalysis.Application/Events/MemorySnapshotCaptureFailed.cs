using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;

namespace DotnetAnalysis.Application.Events;

/// <summary>
/// 表示快照捕获失败，并携带可供上层处理的稳定错误码。
/// </summary>
public sealed record MemorySnapshotCaptureFailed : IApplicationEvent
{
    /// <summary>
    /// 创建捕获失败事件。
    /// </summary>
    /// <param name="sessionId">关联的诊断会话。</param>
    /// <param name="errorCode">稳定错误码。</param>
    /// <param name="message">失败说明。</param>
    /// <param name="occurredAt">事件发生时间。</param>
    /// <param name="source">产生事件的模块名称。</param>
    public MemorySnapshotCaptureFailed(
        ProcessDiagnosticsSessionId sessionId,
        DiagnosticsErrorCode errorCode,
        string message,
        DateTimeOffset occurredAt,
        string source)
    {
        SessionId = sessionId;
        ErrorCode = errorCode;
        Message = message;
        OccurredAt = occurredAt;
        Source = source;
    }

    /// <summary>
    /// 关联的诊断会话。
    /// </summary>
    public ProcessDiagnosticsSessionId SessionId { get; }

    /// <summary>
    /// 稳定错误码。
    /// </summary>
    public DiagnosticsErrorCode ErrorCode { get; }

    /// <summary>
    /// 失败说明。
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// 事件发生时间。
    /// </summary>
    public DateTimeOffset OccurredAt { get; }

    /// <summary>
    /// 产生事件的模块名称。
    /// </summary>
    public string Source { get; }

    /// <summary>
    /// 显式映射到通用事件会话属性。
    /// </summary>
    ProcessDiagnosticsSessionId? IApplicationEvent.SessionId => SessionId;
}
