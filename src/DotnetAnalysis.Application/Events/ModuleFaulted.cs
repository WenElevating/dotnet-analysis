using DotnetAnalysis.Core.Events;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Application.Events;

/// <summary>
/// 表示事件处理器或其他应用模块发生的可报告故障。
/// </summary>
public sealed record ModuleFaulted(
    ProcessDiagnosticsSessionId? SessionId,
    string Module,
    string Message,
    DateTimeOffset OccurredAt,
    string Source) : IApplicationEvent;
