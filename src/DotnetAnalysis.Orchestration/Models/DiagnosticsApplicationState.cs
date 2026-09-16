using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Orchestration.Models;

/// <summary>诊断应用上下文独有的宿主协调阶段。</summary>
public enum DiagnosticsApplicationPhase
{
    /// <summary>尚未建立应用上下文。</summary>
    Start,
    /// <summary>正在协调目标选择。</summary>
    TargetSelection,
    /// <summary>正在协调快照选择。</summary>
    SnapshotSelection,
    /// <summary>应用上下文已关闭。</summary>
    Closed,
    /// <summary>应用上下文发生失败并等待清理。</summary>
    Failed
}

/// <summary>
/// 提供当前目标、会话、快照和操作身份的不可变应用状态快照。
/// </summary>
public sealed record DiagnosticsApplicationState
{
    /// <summary>创建应用状态快照。</summary>
    /// <param name="generation">非空应用上下文代次。</param>
    /// <param name="phase">应用独有协调阶段。</param>
    /// <param name="sessionState">复用 Core 的诊断会话状态。</param>
    /// <param name="target">当前目标上下文。</param>
    /// <param name="sessionId">当前会话身份。</param>
    /// <param name="snapshot">当前快照。</param>
    /// <param name="operation">当前操作。</param>
    /// <param name="quality">当前状态质量。</param>
    /// <param name="failure">当前稳定失败结果。</param>
    public DiagnosticsApplicationState(
        Guid generation,
        DiagnosticsApplicationPhase phase,
        ProcessDiagnosticsSessionState? sessionState = null,
        TargetContext? target = null,
        ProcessDiagnosticsSessionId? sessionId = null,
        MemorySnapshot? snapshot = null,
        DiagnosticOperationState? operation = null,
        DiagnosticQualitySummary? quality = null,
        DiagnosticFailure? failure = null)
    {
        if (generation == Guid.Empty) throw new ArgumentException("Generation cannot be empty.", nameof(generation));
        if (operation is not null && operation.Generation != generation)
        {
            throw new ArgumentException("Operation generation must match application generation.", nameof(operation));
        }

        if (operation is not null && operation.SessionId != sessionId)
        {
            throw new ArgumentException("Session identity must match the operation session identity.", nameof(sessionId));
        }

        if (operation is not null && operation.SnapshotId != snapshot?.Id)
        {
            throw new ArgumentException("Snapshot identity must match the operation snapshot identity.", nameof(snapshot));
        }

        Generation = generation;
        Phase = phase;
        SessionState = sessionState;
        Target = target;
        SessionId = sessionId;
        Snapshot = snapshot;
        Operation = operation;
        Quality = quality;
        Failure = failure;
    }

    /// <summary>应用上下文代次。</summary>
    public Guid Generation { get; }

    /// <summary>应用独有协调阶段。</summary>
    public DiagnosticsApplicationPhase Phase { get; }

    /// <summary>复用 Core 的诊断会话状态。</summary>
    public ProcessDiagnosticsSessionState? SessionState { get; }

    /// <summary>当前目标上下文。</summary>
    public TargetContext? Target { get; }

    /// <summary>当前会话身份。</summary>
    public ProcessDiagnosticsSessionId? SessionId { get; }

    /// <summary>当前快照。</summary>
    public MemorySnapshot? Snapshot { get; }

    /// <summary>当前或最近操作。</summary>
    public DiagnosticOperationState? Operation { get; }

    /// <summary>当前状态质量。</summary>
    public DiagnosticQualitySummary? Quality { get; }

    /// <summary>当前稳定失败结果。</summary>
    public DiagnosticFailure? Failure { get; }
}
