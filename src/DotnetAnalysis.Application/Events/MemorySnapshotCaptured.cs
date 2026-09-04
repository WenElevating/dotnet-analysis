using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;

namespace DotnetAnalysis.Application.Events;

/// <summary>
/// 表示快照捕获和持久化已完成，随后可进入分析阶段。
/// </summary>
public sealed record MemorySnapshotCaptured(
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
