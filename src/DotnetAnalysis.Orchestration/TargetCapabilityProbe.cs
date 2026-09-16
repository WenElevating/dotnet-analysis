using DotnetAnalysis.Application.Contracts.Diagnostics;
using DotnetAnalysis.Orchestration.Models;

namespace DotnetAnalysis.Orchestration;

/// <summary>
/// 将 Application 层能力证据映射为宿主可见的目标上下文。
/// </summary>
public sealed class TargetCapabilityProbe
{
    private readonly IProcessDiagnostics _diagnostics;

    /// <summary>
    /// 创建目标能力探测器。
    /// </summary>
    /// <param name="diagnostics">提供稳定能力证据的 Application 契约。</param>
    public TargetCapabilityProbe(IProcessDiagnostics diagnostics)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    /// <summary>
    /// 探测目标能力并创建目标上下文；该结果不能替代附着前的实时身份验证。
    /// </summary>
    /// <param name="target">已枚举的目标进程身份。</param>
    /// <param name="cancellationToken">取消探测的令牌。</param>
    /// <returns>包含运行时、架构和逐项能力状态的目标上下文。</returns>
    public async Task<TargetContext> ProbeAsync(
        DotnetAnalysis.Core.Diagnostics.TargetProcess target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var evidence = await _diagnostics.ProbeCapabilitiesAsync(target, cancellationToken).ConfigureAwait(false);
        if (evidence.Target.ProcessId != target.ProcessId
            || evidence.Target.StartedAtUtc != target.StartedAtUtc)
        {
            throw new DiagnosticsException(
                DiagnosticsErrorCode.TargetChanged,
                "能力探测返回的目标身份已发生变化。");
        }

        var runtime = evidence.RuntimeMajorVersion > 0
            ? $".NET {evidence.RuntimeMajorVersion}"
            : null;
        var architecture = evidence.Is64BitTarget
            ? TargetArchitecture.X64
            : TargetArchitecture.Unknown;
        var capabilities = new DiagnosticCapabilities(
            CreateAvailability(
                evidence.StandardSnapshotAvailable,
                evidence.StandardSnapshotErrorCode,
                evidence.StandardSnapshotReason),
            CreateAvailability(
                evidence.RetentionSnapshotAvailable,
                evidence.RetentionSnapshotErrorCode,
                evidence.RetentionSnapshotReason),
            CreateAvailability(
                evidence.ExecutionSamplingAvailable,
                evidence.ExecutionSamplingErrorCode,
                evidence.ExecutionSamplingReason),
            CreateAvailability(
                evidence.StandardSnapshotAvailable,
                evidence.StandardSnapshotErrorCode,
                evidence.StandardSnapshotReason),
            CreateAvailability(
                evidence.StandardSnapshotAvailable,
                evidence.StandardSnapshotErrorCode,
                evidence.StandardSnapshotReason),
            evidence.CheckedAtUtc);

        return new TargetContext(target, runtime, architecture, capabilities);
    }

    private static DiagnosticCapabilityAvailability CreateAvailability(
        bool isAvailable,
        DiagnosticsErrorCode? errorCode,
        string? reason) => new(isAvailable, reason, errorCode);
}
