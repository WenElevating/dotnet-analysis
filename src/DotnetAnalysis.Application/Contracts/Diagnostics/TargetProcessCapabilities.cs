using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Application.Contracts.Diagnostics;

/// <summary>
/// 表示 Diagnostics 层对目标进程执行的无副作用能力探测证据。
/// </summary>
public sealed record TargetProcessCapabilities
{
    /// <summary>
    /// 创建目标进程能力证据。
    /// </summary>
    /// <param name="target">被探测的目标进程身份。</param>
    /// <param name="isWindows">探测环境是否为 Windows。</param>
    /// <param name="is64BitOperatingSystem">探测环境是否为 64 位操作系统。</param>
    /// <param name="is64BitTarget">目标进程是否为 64 位。</param>
    /// <param name="isCoreClr">目标是否加载 CoreCLR。</param>
    /// <param name="runtimeMajorVersion">目标运行时主版本；未知时为零。</param>
    /// <param name="standardSnapshotAvailable">标准快照能力是否可用。</param>
    /// <param name="standardSnapshotErrorCode">标准快照不可用时的稳定错误码。</param>
    /// <param name="standardSnapshotReason">标准快照不可用时的原因。</param>
    /// <param name="retentionSnapshotAvailable">Retention 快照能力是否可用。</param>
    /// <param name="retentionSnapshotErrorCode">Retention 快照不可用时的稳定错误码。</param>
    /// <param name="retentionSnapshotReason">Retention 快照不可用时的原因。</param>
    /// <param name="executionSamplingAvailable">执行采样能力是否可用。</param>
    /// <param name="executionSamplingErrorCode">执行采样不可用时的稳定错误码。</param>
    /// <param name="executionSamplingReason">执行采样不可用时的原因。</param>
    /// <param name="checkedAtUtc">能力证据完成探测的 UTC 时间。</param>
    public TargetProcessCapabilities(
        TargetProcess target,
        bool isWindows,
        bool is64BitOperatingSystem,
        bool is64BitTarget,
        bool isCoreClr,
        int runtimeMajorVersion,
        bool standardSnapshotAvailable,
        DiagnosticsErrorCode? standardSnapshotErrorCode,
        string? standardSnapshotReason,
        bool retentionSnapshotAvailable,
        DiagnosticsErrorCode? retentionSnapshotErrorCode,
        string? retentionSnapshotReason,
        bool executionSamplingAvailable,
        DiagnosticsErrorCode? executionSamplingErrorCode,
        string? executionSamplingReason,
        DateTimeOffset checkedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (runtimeMajorVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(runtimeMajorVersion),
                runtimeMajorVersion,
                "Runtime major version cannot be negative.");
        }

        Target = target;
        IsWindows = isWindows;
        Is64BitOperatingSystem = is64BitOperatingSystem;
        Is64BitTarget = is64BitTarget;
        IsCoreClr = isCoreClr;
        RuntimeMajorVersion = runtimeMajorVersion;
        StandardSnapshotAvailable = standardSnapshotAvailable;
        StandardSnapshotErrorCode = standardSnapshotErrorCode;
        StandardSnapshotReason = NormalizeReason(standardSnapshotReason);
        RetentionSnapshotAvailable = retentionSnapshotAvailable;
        RetentionSnapshotErrorCode = retentionSnapshotErrorCode;
        RetentionSnapshotReason = NormalizeReason(retentionSnapshotReason);
        ExecutionSamplingAvailable = executionSamplingAvailable;
        ExecutionSamplingErrorCode = executionSamplingErrorCode;
        ExecutionSamplingReason = NormalizeReason(executionSamplingReason);
        CheckedAtUtc = checkedAtUtc;
    }

    /// <summary>被探测的目标进程身份。</summary>
    public TargetProcess Target { get; }

    /// <summary>探测环境是否为 Windows。</summary>
    public bool IsWindows { get; }

    /// <summary>探测环境是否为 64 位操作系统。</summary>
    public bool Is64BitOperatingSystem { get; }

    /// <summary>目标进程是否为 64 位。</summary>
    public bool Is64BitTarget { get; }

    /// <summary>目标进程是否加载 CoreCLR。</summary>
    public bool IsCoreClr { get; }

    /// <summary>目标运行时主版本；未知时为零。</summary>
    public int RuntimeMajorVersion { get; }

    /// <summary>标准快照能力是否可用。</summary>
    public bool StandardSnapshotAvailable { get; }

    /// <summary>标准快照不可用时的稳定错误码。</summary>
    public DiagnosticsErrorCode? StandardSnapshotErrorCode { get; }

    /// <summary>标准快照能力限制原因。</summary>
    public string? StandardSnapshotReason { get; }

    /// <summary>Retention 快照能力是否可用。</summary>
    public bool RetentionSnapshotAvailable { get; }

    /// <summary>Retention 快照不可用时的稳定错误码。</summary>
    public DiagnosticsErrorCode? RetentionSnapshotErrorCode { get; }

    /// <summary>Retention 快照能力限制原因。</summary>
    public string? RetentionSnapshotReason { get; }

    /// <summary>执行采样能力是否可用。</summary>
    public bool ExecutionSamplingAvailable { get; }

    /// <summary>执行采样不可用时的稳定错误码。</summary>
    public DiagnosticsErrorCode? ExecutionSamplingErrorCode { get; }

    /// <summary>执行采样能力限制原因。</summary>
    public string? ExecutionSamplingReason { get; }

    /// <summary>能力证据完成探测的 UTC 时间。</summary>
    public DateTimeOffset CheckedAtUtc { get; }

    private static string? NormalizeReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
}
