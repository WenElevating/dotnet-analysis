using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Application.Contracts.Diagnostics;

/// <summary>
/// 表示目标进程已启动并完成身份读取后的稳定结果。
/// </summary>
public sealed record TargetProcessLaunchResult
{
    /// <summary>创建启动结果。</summary>
    /// <param name="target">启动目标的进程身份。</param>
    /// <param name="identityValidated">是否已成功读取并确认身份。</param>
    public TargetProcessLaunchResult(TargetProcess target, bool identityValidated)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        if (!identityValidated)
        {
            throw new ArgumentException("A launch result must contain a validated process identity.", nameof(identityValidated));
        }

        IdentityValidated = identityValidated;
    }

    /// <summary>启动目标的进程身份。</summary>
    public TargetProcess Target { get; }

    /// <summary>是否已完成身份验证。</summary>
    public bool IdentityValidated { get; }
}
