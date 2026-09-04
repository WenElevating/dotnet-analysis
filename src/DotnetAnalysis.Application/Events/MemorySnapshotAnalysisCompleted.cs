using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;

namespace DotnetAnalysis.Application.Events;

/// <summary>
/// 表示指定快照分析成功完成。
/// </summary>
public sealed record MemorySnapshotAnalysisCompleted(
    ProcessDiagnosticsSessionId SessionId,
    MemorySnapshotId SnapshotId,
    DateTimeOffset OccurredAt,
    string Source) : IApplicationEvent
{
    /// <summary>
    /// 此事件关联的会话。
    /// </summary>
    ProcessDiagnosticsSessionId? IApplicationEvent.SessionId => SessionId;
}
