using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Application.Events;

/// <summary>
/// 为应用事件提供可选的编排代次和操作身份。
/// </summary>
public interface IOperationEvent
{
    /// <summary>产生事件的应用上下文代次；兼容旧事件时为空 GUID。</summary>
    Guid Generation { get; }

    /// <summary>产生事件的操作身份；非操作事件时为空。</summary>
    Guid? OperationId { get; }

    /// <summary>验证事件是否属于指定操作上下文。</summary>
    /// <param name="generation">宿主当前代次。</param>
    /// <param name="sessionId">宿主当前会话。</param>
    /// <param name="operationId">宿主当前操作。</param>
    /// <returns>身份全部匹配时返回 <see langword="true"/>。</returns>
    bool Matches(Guid generation, ProcessDiagnosticsSessionId? sessionId, Guid operationId);
}
