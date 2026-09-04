using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Core.Events;

/// <summary>
/// 所有跨应用层通知共享的诊断事件契约。
/// </summary>
public interface IApplicationEvent
{
    /// <summary>
    /// 事件发生时间。
    /// </summary>
    DateTimeOffset OccurredAt { get; }

    /// <summary>
    /// 关联的诊断会话；全局事件可为空。
    /// </summary>
    ProcessDiagnosticsSessionId? SessionId { get; }

    /// <summary>
    /// 产生事件的模块或组件名称。
    /// </summary>
    string Source { get; }
}
