using DotnetAnalysis.Application.Contracts.Diagnostics;

namespace DotnetAnalysis.Orchestration.Models;

/// <summary>
/// 表示一项诊断能力是否可用及其限制原因。
/// </summary>
public sealed record DiagnosticCapabilityAvailability
{
    /// <summary>创建能力状态。</summary>
    /// <param name="isAvailable">能力是否可用。</param>
    /// <param name="reason">不可用或受限原因；可用时为空。</param>
    /// <param name="errorCode">关联的稳定错误码；没有时为空。</param>
    public DiagnosticCapabilityAvailability(
        bool isAvailable,
        string? reason = null,
        DiagnosticsErrorCode? errorCode = null)
    {
        IsAvailable = isAvailable;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        ErrorCode = errorCode;
    }

    /// <summary>能力是否可用。</summary>
    public bool IsAvailable { get; }

    /// <summary>能力限制或失败原因。</summary>
    public string? Reason { get; }

    /// <summary>关联的稳定错误码。</summary>
    public DiagnosticsErrorCode? ErrorCode { get; }
}

/// <summary>
/// 汇总目标可执行的诊断能力；每项能力独立记录，不因单项失败而整体失效。
/// </summary>
public sealed record DiagnosticCapabilities
{
    /// <summary>创建能力摘要。</summary>
    /// <param name="standardSnapshot">标准快照能力。</param>
    /// <param name="retentionSnapshot">Retention 快照能力。</param>
    /// <param name="executionSampling">执行采样能力。</param>
    /// <param name="pagedObjectQueries">对象分页查询能力。</param>
    /// <param name="referencePathQueries">引用路径查询能力。</param>
    /// <param name="checkedAtUtc">能力探测完成时间。</param>
    public DiagnosticCapabilities(
        DiagnosticCapabilityAvailability standardSnapshot,
        DiagnosticCapabilityAvailability retentionSnapshot,
        DiagnosticCapabilityAvailability executionSampling,
        DiagnosticCapabilityAvailability pagedObjectQueries,
        DiagnosticCapabilityAvailability referencePathQueries,
        DateTimeOffset checkedAtUtc)
    {
        StandardSnapshot = standardSnapshot ?? throw new ArgumentNullException(nameof(standardSnapshot));
        RetentionSnapshot = retentionSnapshot ?? throw new ArgumentNullException(nameof(retentionSnapshot));
        ExecutionSampling = executionSampling ?? throw new ArgumentNullException(nameof(executionSampling));
        PagedObjectQueries = pagedObjectQueries ?? throw new ArgumentNullException(nameof(pagedObjectQueries));
        ReferencePathQueries = referencePathQueries ?? throw new ArgumentNullException(nameof(referencePathQueries));
        CheckedAtUtc = checkedAtUtc;
    }

    /// <summary>标准 GCDump 快照能力。</summary>
    public DiagnosticCapabilityAvailability StandardSnapshot { get; }

    /// <summary>Retention 快照能力。</summary>
    public DiagnosticCapabilityAvailability RetentionSnapshot { get; }

    /// <summary>执行采样能力。</summary>
    public DiagnosticCapabilityAvailability ExecutionSampling { get; }

    /// <summary>对象分页查询能力。</summary>
    public DiagnosticCapabilityAvailability PagedObjectQueries { get; }

    /// <summary>引用路径查询能力。</summary>
    public DiagnosticCapabilityAvailability ReferencePathQueries { get; }

    /// <summary>能力摘要探测完成时间。</summary>
    public DateTimeOffset CheckedAtUtc { get; }
}
