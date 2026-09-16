namespace DotnetAnalysis.Orchestration.Operations;

/// <summary>
/// 定义诊断操作可观察的编排阶段。
/// </summary>
public enum OperationStage
{
    /// <summary>操作尚未开始。</summary>
    None,
    /// <summary>正在准备操作。</summary>
    Starting,
    /// <summary>正在查找目标。</summary>
    FindingTarget,
    /// <summary>正在附着诊断会话。</summary>
    Attaching,
    /// <summary>正在采样目标。</summary>
    Sampling,
    /// <summary>正在捕获快照。</summary>
    Capturing,
    /// <summary>正在分析快照。</summary>
    Analyzing,
    /// <summary>正在发布操作结果。</summary>
    Publishing,
    /// <summary>正在完成操作。</summary>
    Completing,
    /// <summary>操作已完成。</summary>
    Completed
}
