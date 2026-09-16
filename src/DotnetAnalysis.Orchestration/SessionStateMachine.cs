using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Orchestration;

/// <summary>
/// 负责校验活动分析会话的生命周期状态迁移。
/// </summary>
public sealed class SessionStateMachine
{
    private readonly object _gate = new();
    private ProcessDiagnosticsSessionState _state = ProcessDiagnosticsSessionState.Attaching;

    /// <summary>当前会话状态。</summary>
    public ProcessDiagnosticsSessionState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>尝试迁移到指定状态。</summary>
    /// <param name="nextState">目标状态。</param>
    /// <returns>迁移成功或目标状态已经是当前状态时返回 <see langword="true"/>。</returns>
    public bool TryMoveTo(ProcessDiagnosticsSessionState nextState)
    {
        lock (_gate)
        {
            if (_state == nextState) return true;
            if (!ProcessDiagnosticsSessionTransitionRules.CanMove(_state, nextState)) return false;
            _state = nextState;
            return true;
        }
    }

    /// <summary>迁移到指定状态；非法迁移时抛出异常。</summary>
    /// <param name="nextState">目标状态。</param>
    public void MoveTo(ProcessDiagnosticsSessionState nextState)
    {
        if (!TryMoveTo(nextState))
        {
            throw new InvalidOperationException($"Cannot move analysis session from {State} to {nextState}.");
        }
    }
}
