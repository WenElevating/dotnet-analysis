namespace DotnetAnalysis.Orchestration.Models;

/// <summary>诊断证据的稳定质量级别。</summary>
public enum DiagnosticQuality
{
    /// <summary>质量尚未确定。</summary>
    Unknown,
    /// <summary>所需证据完整且可验证。</summary>
    Complete,
    /// <summary>仅有部分证据，结果仍可使用但存在明确缺失。</summary>
    Partial,
    /// <summary>当前能力或输入不足以产生该证据。</summary>
    Unavailable,
    /// <summary>处理失败，不能把结果视为成功证据。</summary>
    Failed
}

/// <summary>
/// 描述一次诊断结果的质量、缺失信息和可见说明。
/// </summary>
public sealed record DiagnosticQualitySummary
{
    /// <summary>创建质量摘要。</summary>
    /// <param name="quality">质量级别。</param>
    /// <param name="summary">面向宿主的质量说明。</param>
    /// <param name="missingEvidence">缺失证据说明。</param>
    public DiagnosticQualitySummary(
        DiagnosticQuality quality,
        string? summary = null,
        IReadOnlyList<string>? missingEvidence = null)
    {
        Quality = quality;
        Summary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim();
        MissingEvidence = missingEvidence is null
            ? Array.Empty<string>()
            : missingEvidence.Where(static item => !string.IsNullOrWhiteSpace(item)).Select(static item => item.Trim()).ToArray();
    }

    /// <summary>质量级别。</summary>
    public DiagnosticQuality Quality { get; }

    /// <summary>质量说明。</summary>
    public string? Summary { get; }

    /// <summary>缺失或无法验证的证据列表。</summary>
    public IReadOnlyList<string> MissingEvidence { get; }
}
