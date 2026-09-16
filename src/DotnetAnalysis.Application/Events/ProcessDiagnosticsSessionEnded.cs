using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;

namespace DotnetAnalysis.Application.Events;

/// <summary>
/// 表示诊断会话已结束并停止后台工作。
/// </summary>
public sealed record ProcessDiagnosticsSessionEnded(
    ProcessDiagnosticsSessionId SessionId,
    DateTimeOffset OccurredAt,
    string Source) : IApplicationEvent, IOperationEvent
{
    /// <summary>
    /// 此事件关联的会话。
    /// </summary>
    ProcessDiagnosticsSessionId? IApplicationEvent.SessionId => SessionId;

    /// <inheritdoc />
    public Guid Generation { get; init; }

    /// <inheritdoc />
    public Guid? OperationId { get; init; }

    /// <inheritdoc />
    public bool Matches(Guid generation, ProcessDiagnosticsSessionId? sessionId, Guid operationId) =>
        Generation == generation && SessionId == sessionId && OperationId == operationId;
}
