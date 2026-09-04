using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;

namespace DotnetAnalysis.Application.Events;

/// <summary>
/// 表示快照分析失败，并携带可供上层处理的稳定错误码。
/// </summary>
public sealed record MemorySnapshotAnalysisFailed : IApplicationEvent
{
    /// <summary>
    /// 创建分析失败事件。
    /// </summary>
    /// <param name="sessionId">关联的诊断会话。</param>
    /// <param name="snapshotId">失败的快照。</param>
    /// <param name="errorCode">稳定错误码。</param>
    /// <param name="message">失败说明。</param>
    /// <param name="occurredAt">事件发生时间。</param>
    /// <param name="source">产生事件的模块名称。</param>
    public MemorySnapshotAnalysisFailed(
        ProcessDiagnosticsSessionId sessionId,
        MemorySnapshotId snapshotId,
        DiagnosticsErrorCode errorCode,
        string message,
        DateTimeOffset occurredAt,
        string source)
    {
        SessionId = sessionId;
        SnapshotId = snapshotId;
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
    /// 失败的快照标识。
    /// </summary>
    public MemorySnapshotId SnapshotId { get; }

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
