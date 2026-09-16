using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Orchestration.Models;

/// <summary>诊断应用上下文的稳定生命周期状态。</summary>
public enum DiagnosticsApplicationLifecycle
{
    /// <summary>尚未进入目标或快照分析。</summary>
    Start,
    /// <summary>正在查找目标。</summary>
    FindingTarget,
    /// <summary>正在附着目标。</summary>
    Attaching,
    /// <summary>活动会话正在分析。</summary>
    LiveAnalysis,
    /// <summary>正在捕获快照。</summary>
    CapturingSnapshot,
    /// <summary>正在分析快照。</summary>
    AnalyzingSnapshot,
    /// <summary>当前快照可查询。</summary>
    SnapshotAnalysis,
    /// <summary>正在停止或替换上下文。</summary>
    Stopping,
    /// <summary>已正常结束。</summary>
    Ended,
    /// <summary>发生稳定失败。</summary>
    Failed,
    /// <summary>应用上下文已关闭。</summary>
    Closed
}

/// <summary>
/// 提供当前目标、会话、快照和操作身份的不可变应用状态快照。
/// </summary>
public sealed record DiagnosticsApplicationState
{
    /// <summary>创建应用状态快照。</summary>
    /// <param name="generation">非空应用上下文代次。</param>
    /// <param name="lifecycle">应用生命周期状态。</param>
    /// <param name="target">当前目标上下文。</param>
    /// <param name="sessionId">当前会话身份。</param>
    /// <param name="snapshot">当前快照。</param>
    /// <param name="operation">当前操作。</param>
    /// <param name="quality">当前状态质量。</param>
    /// <param name="failure">当前稳定失败结果。</param>
    public DiagnosticsApplicationState(
        Guid generation,
        DiagnosticsApplicationLifecycle lifecycle,
        TargetContext? target = null,
        ProcessDiagnosticsSessionId? sessionId = null,
        MemorySnapshot? snapshot = null,
        DiagnosticOperationState? operation = null,
        DiagnosticQualitySummary? quality = null,
        DiagnosticFailure? failure = null)
    {
        if (generation == Guid.Empty) throw new ArgumentException("Generation cannot be empty.", nameof(generation));
        if (snapshot is not null && operation?.SnapshotId is not null && operation.SnapshotId != snapshot.Id)
        {
            throw new ArgumentException("Snapshot identity must match the operation snapshot identity.", nameof(snapshot));
        }

        Generation = generation;
        Lifecycle = lifecycle;
        Target = target;
        SessionId = sessionId;
        Snapshot = snapshot;
        Operation = operation;
        Quality = quality;
        Failure = failure;
    }

    /// <summary>应用上下文代次。</summary>
    public Guid Generation { get; }

    /// <summary>应用生命周期状态。</summary>
    public DiagnosticsApplicationLifecycle Lifecycle { get; }

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
