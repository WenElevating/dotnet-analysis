using DotnetAnalysis.Core.Diagnostics;
using DotnetAnalysis.Orchestration.Models;

namespace DotnetAnalysis.Orchestration.Operations;

/// <summary>
/// 配置诊断操作的时钟、截止时间和外部取消边界。
/// </summary>
public sealed record OperationOptions
{
    /// <summary>创建操作选项。</summary>
    /// <param name="timeout">从创建时刻开始计算的总超时时长；为空表示无内部截止时间。</param>
    /// <param name="timeProvider">提供当前时间和定时器的时钟。</param>
    public OperationOptions(TimeSpan? timeout = null, TimeProvider? timeProvider = null)
    {
        if (timeout is not null && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Operation timeout must be greater than zero.");
        }

        Timeout = timeout;
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>操作总超时时长。</summary>
    public TimeSpan? Timeout { get; }

    /// <summary>操作使用的时钟。</summary>
    public TimeProvider TimeProvider { get; }
}

/// <summary>
/// 表示诊断操作的稳定、线程安全状态和结果。
/// </summary>
public sealed class DiagnosticOperation
{
    private readonly object _gate = new();
    private readonly DateTimeOffset _startedAtUtc;
    private OperationStage _stage;
    private double? _progress;
    private DiagnosticOperationStatus _status;
    private object? _result;
    private DiagnosticFailure? _failure;
    private DateTimeOffset? _completedAtUtc;

    /// <summary>创建一个具有代次隔离身份的诊断操作。</summary>
    /// <param name="generation">应用上下文代次。</param>
    /// <param name="sessionId">关联的诊断会话；全局操作可为空。</param>
    /// <param name="deadlineUtc">操作截止时间；没有截止时间时为空。</param>
    public DiagnosticOperation(Guid generation, ProcessDiagnosticsSessionId? sessionId, DateTimeOffset? deadlineUtc = null)
    {
        if (generation == Guid.Empty) throw new ArgumentException("Generation cannot be empty.", nameof(generation));
        OperationId = Guid.NewGuid();
        Generation = generation;
        SessionId = sessionId;
        DeadlineUtc = deadlineUtc;
        _startedAtUtc = DateTimeOffset.UtcNow;
        _stage = OperationStage.None;
        _status = DiagnosticOperationStatus.Pending;
    }

    /// <summary>操作唯一身份。</summary>
    public Guid OperationId { get; }

    /// <summary>创建操作时所属的应用代次。</summary>
    public Guid Generation { get; }

    /// <summary>关联的诊断会话。</summary>
    public ProcessDiagnosticsSessionId? SessionId { get; }

    /// <summary>操作创建时间。</summary>
    public DateTimeOffset StartedAtUtc => _startedAtUtc;

    /// <summary>操作总截止时间。</summary>
    public DateTimeOffset? DeadlineUtc { get; }

    /// <summary>当前操作阶段。</summary>
    public OperationStage Stage { get { lock (_gate) return _stage; } }

    /// <summary>当前进度；无法估计时为空。</summary>
    public double? Progress { get { lock (_gate) return _progress; } }

    /// <summary>当前操作状态。</summary>
    public DiagnosticOperationStatus Status { get { lock (_gate) return _status; } }

    /// <summary>稳定完成结果；未成功完成时为空。</summary>
    public object? Result { get { lock (_gate) return _result; } }

    /// <summary>稳定失败结果。</summary>
    public DiagnosticFailure? Failure { get { lock (_gate) return _failure; } }

    /// <summary>操作进入终态的时间；尚未结束时为空。</summary>
    public DateTimeOffset? CompletedAtUtc { get { lock (_gate) return _completedAtUtc; } }

    /// <summary>进入运行状态；重复调用保持幂等。</summary>
    public bool TryStart() => Transition(DiagnosticOperationStatus.Running, OperationStage.Starting, null, null);

    /// <summary>更新当前阶段和可选进度。</summary>
    /// <param name="stage">新的操作阶段。</param>
    /// <param name="progress">0 到 1 之间的进度；未知时为空。</param>
    public bool TrySetStage(OperationStage stage, double? progress = null)
    {
        if (progress is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(progress));
        lock (_gate)
        {
            if (_status is not (DiagnosticOperationStatus.Pending or DiagnosticOperationStatus.Running)) return false;
            _stage = stage;
            _progress = progress;
            return true;
        }
    }

    /// <summary>以成功结果结束操作；终态上的重复调用不会覆盖第一次结果。</summary>
    /// <param name="result">供宿主读取的稳定结果。</param>
    public bool TryComplete(object? result = null) => Transition(DiagnosticOperationStatus.Succeeded, OperationStage.Completed, result, null);

    /// <summary>以稳定失败结果结束操作。</summary>
    /// <param name="failure">失败阶段、错误码和重试语义。</param>
    public bool TryFail(DiagnosticFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_gate)
        {
            if (_status is DiagnosticOperationStatus.Succeeded
                or DiagnosticOperationStatus.Canceled
                or DiagnosticOperationStatus.TimedOut
                or DiagnosticOperationStatus.Failed)
            {
                return false;
            }

            _status = DiagnosticOperationStatus.Failed;
            _stage = failure.Stage == DiagnosticOperationStage.None ? _stage : MapStage(failure.Stage);
            _result = null;
            _failure = failure;
            _completedAtUtc = DateTimeOffset.UtcNow;
            return true;
        }
    }

    /// <summary>以取消状态结束操作；终态上的重复调用不会改变结果。</summary>
    public bool TryCancel() => Transition(DiagnosticOperationStatus.Canceled, _stage, null, null);

    internal bool TryTimeout() => Transition(DiagnosticOperationStatus.TimedOut, _stage, null, null);

    private bool Transition(DiagnosticOperationStatus status, OperationStage stage, object? result, DiagnosticFailure? failure)
    {
        lock (_gate)
        {
            if (_status is DiagnosticOperationStatus.Succeeded
                or DiagnosticOperationStatus.Canceled
                or DiagnosticOperationStatus.TimedOut
                or DiagnosticOperationStatus.Failed)
            {
                return false;
            }

            _status = status;
            _stage = stage;
            _progress = status == DiagnosticOperationStatus.Succeeded ? 1 : _progress;
            _result = result;
            _failure = failure;
            _completedAtUtc = DateTimeOffset.UtcNow;
            return true;
        }
    }

    private static OperationStage MapStage(DiagnosticOperationStage stage) => stage switch
    {
        DiagnosticOperationStage.FindingTarget => OperationStage.FindingTarget,
        DiagnosticOperationStage.Querying => OperationStage.Analyzing,
        DiagnosticOperationStage.Closing => OperationStage.Completing,
        _ => OperationStage.None
    };
}
