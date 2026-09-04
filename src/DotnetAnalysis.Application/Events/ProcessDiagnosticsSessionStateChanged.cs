using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Core.Events;

namespace DotnetAnalysis.Application.Events;

/// <summary>
/// 表示诊断会话生命周期状态发生变化。
/// </summary>
public sealed record ProcessDiagnosticsSessionStateChanged(
    ProcessDiagnosticsSessionId SessionId,
    ProcessDiagnosticsSessionState State,
    DateTimeOffset OccurredAt,
    string Source) : IApplicationEvent
{
    /// <summary>
    /// 此事件关联的会话。
    /// </summary>
    ProcessDiagnosticsSessionId? IApplicationEvent.SessionId => SessionId;
}
