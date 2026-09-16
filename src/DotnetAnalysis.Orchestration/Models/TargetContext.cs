using DotnetAnalysis.Core.Diagnostics;

namespace DotnetAnalysis.Orchestration.Models;

/// <summary>
/// 表示已经确认身份并完成基础能力描述的诊断目标上下文。
/// </summary>
public sealed record TargetContext
{
    /// <summary>
    /// 创建目标上下文；目标身份由进程 ID 和启动时间共同组成。
    /// </summary>
    /// <param name="target">已枚举的目标进程身份。</param>
    /// <param name="runtime">可识别的 CoreCLR 运行时名称；无法识别时为空。</param>
    /// <param name="architecture">目标进程架构。</param>
    /// <param name="capabilities">当前目标的能力摘要。</param>
    public TargetContext(
        TargetProcess target,
        string? runtime,
        TargetArchitecture architecture,
        DiagnosticCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capabilities);

        Target = target;
        Runtime = string.IsNullOrWhiteSpace(runtime) ? null : runtime.Trim();
        Architecture = architecture;
        Capabilities = capabilities;
    }

    /// <summary>已确认的目标进程身份。</summary>
    public TargetProcess Target { get; }

    /// <summary>目标进程 ID。</summary>
    public int ProcessId => Target.ProcessId;

    /// <summary>目标进程启动时间。</summary>
    public DateTimeOffset StartedAtUtc => Target.StartedAtUtc;

    /// <summary>目标进程显示名称。</summary>
    public string ProcessName => Target.ProcessName;

    /// <summary>目标 CoreCLR 运行时名称。</summary>
    public string? Runtime { get; }

    /// <summary>目标进程架构。</summary>
    public TargetArchitecture Architecture { get; }

    /// <summary>目标当前诊断能力。</summary>
    public DiagnosticCapabilities Capabilities { get; }
}

/// <summary>
/// 目标进程的稳定架构分类。
/// </summary>
public enum TargetArchitecture
{
    /// <summary>尚未识别架构。</summary>
    Unknown,
    /// <summary>x86 架构。</summary>
    X86,
    /// <summary>x64 架构。</summary>
    X64,
    /// <summary>Arm64 架构。</summary>
    Arm64
}
