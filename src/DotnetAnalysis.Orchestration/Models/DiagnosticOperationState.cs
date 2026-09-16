using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Orchestration.Models;

/// <summary>诊断操作所处的稳定业务阶段。</summary>
public enum DiagnosticOperationStage
{
    /// <summary>尚未进入目标或快照处理。</summary>
    None,
    /// <summary>正在查找目标。</summary>
    FindingTarget,
    /// <summary>正在查询结果。</summary>
    Querying,
    /// <summary>正在关闭应用上下文。</summary>
    Closing
}

/// <summary>诊断操作的稳定处理状态。</summary>
public enum DiagnosticOperationStatus
{
    /// <summary>已创建但尚未开始。</summary>
    Pending,
    /// <summary>正在执行。</summary>
    Running,
    /// <summary>成功完成。</summary>
    Succeeded,
    /// <summary>被调用方取消。</summary>
    Canceled,
    /// <summary>超过截止时间。</summary>
    TimedOut,
    /// <summary>执行失败。</summary>
    Failed
}

/// <summary>保存稳定错误码、阶段和重试语义的操作失败结果。</summary>
public sealed record DiagnosticFailure
{
    /// <summary>创建失败结果。</summary>
    /// <param name="errorCode">稳定错误码。</param>
    /// <param name="stage">发生失败的业务阶段。</param>
    /// <param name="retryable">宿主是否可以重试当前操作。</param>
    /// <param name="message">面向宿主的稳定说明。</param>
    public DiagnosticFailure(
        DiagnosticsErrorCode errorCode,
        DiagnosticOperationStage stage,
        bool retryable,
        string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ErrorCode = errorCode;
        Stage = stage;
        Retryable = retryable;
        Message = message.Trim();
    }

    /// <summary>稳定错误码。</summary>
    public DiagnosticsErrorCode ErrorCode { get; }

    /// <summary>失败阶段。</summary>
    public DiagnosticOperationStage Stage { get; }

    /// <summary>是否允许宿主重试。</summary>
    public bool Retryable { get; }

    /// <summary>稳定错误说明。</summary>
    public string Message { get; }
}

/// <summary>
/// 描述一个可被宿主观察的诊断操作及其代次隔离身份。
/// </summary>
public sealed record DiagnosticOperationState
{
    /// <summary>创建操作状态快照。</summary>
    /// <param name="operationId">非空操作身份。</param>
    /// <param name="generation">应用上下文代次。</param>
    /// <param name="stage">当前业务阶段。</param>
    /// <param name="status">当前处理状态。</param>
    /// <param name="sessionId">关联会话身份。</param>
    /// <param name="snapshotId">关联快照身份。</param>
    /// <param name="captureMode">快照捕获模式；非捕获操作时为空。</param>
    /// <param name="deadlineUtc">操作总截止时间。</param>
    /// <param name="isCancellable">当前操作是否可取消。</param>
    /// <param name="failure">稳定失败结果。</param>
    public DiagnosticOperationState(
        Guid operationId,
        Guid generation,
        DiagnosticOperationStage stage,
        DiagnosticOperationStatus status,
        ProcessDiagnosticsSessionId? sessionId = null,
        MemorySnapshotId? snapshotId = null,
        MemorySnapshotCaptureMode? captureMode = null,
        DateTimeOffset? deadlineUtc = null,
        bool isCancellable = false,
        DiagnosticFailure? failure = null)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("Operation ID cannot be empty.", nameof(operationId));
        if (generation == Guid.Empty) throw new ArgumentException("Generation cannot be empty.", nameof(generation));
        if (deadlineUtc is not null && deadlineUtc <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentOutOfRangeException(nameof(deadlineUtc), deadlineUtc, "Deadline must be in the future.");
        }

        OperationId = operationId;
        Generation = generation;
        Stage = stage;
        Status = status;
        SessionId = sessionId;
        SnapshotId = snapshotId;
        CaptureMode = captureMode;
        DeadlineUtc = deadlineUtc;
        IsCancellable = isCancellable;
        Failure = failure;
    }

    /// <summary>操作唯一身份。</summary>
    public Guid OperationId { get; }

    /// <summary>应用上下文代次。</summary>
    public Guid Generation { get; }

    /// <summary>当前操作阶段。</summary>
    public DiagnosticOperationStage Stage { get; }

    /// <summary>当前处理状态。</summary>
    public DiagnosticOperationStatus Status { get; }

    /// <summary>关联会话身份。</summary>
    public ProcessDiagnosticsSessionId? SessionId { get; }

    /// <summary>关联快照身份。</summary>
    public MemorySnapshotId? SnapshotId { get; }

    /// <summary>请求的快照捕获模式；非捕获操作时为空。</summary>
    public MemorySnapshotCaptureMode? CaptureMode { get; }

    /// <summary>操作总截止时间。</summary>
    public DateTimeOffset? DeadlineUtc { get; }

    /// <summary>当前操作是否可取消。</summary>
    public bool IsCancellable { get; }

    /// <summary>稳定失败结果。</summary>
    public DiagnosticFailure? Failure { get; }
}
