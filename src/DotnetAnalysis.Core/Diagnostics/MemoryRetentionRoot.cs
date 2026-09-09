namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 描述一条保留路径的 GC 根证据及其可选的持有函数信息。
/// </summary>
public sealed record MemoryRetentionRoot
{
    /// <summary>
    /// 创建 GC 根证据。
    /// </summary>
    /// <param name="kind">CLR 报告的根类别。</param>
    /// <param name="flags">CLR 报告的根标志。</param>
    /// <param name="functionName">仅栈根可提供的持有函数全名；无法验证时为空。</param>
    /// <param name="moduleName">函数所属模块名；函数名为空时必须为空。</param>
    /// <exception cref="ArgumentException">非栈根提供函数名、函数名为空白或模块名与函数名状态不一致时引发。</exception>
    public MemoryRetentionRoot(
        MemoryRootKind kind,
        MemoryRootFlags flags,
        string? functionName,
        string? moduleName)
    {
        if (kind is not MemoryRootKind.Stack && !string.IsNullOrWhiteSpace(functionName))
        {
            throw new ArgumentException("只有 CLR 栈根可以携带已验证的持有函数。", nameof(functionName));
        }

        if (string.IsNullOrWhiteSpace(functionName))
        {
            if (!string.IsNullOrWhiteSpace(moduleName))
            {
                throw new ArgumentException("模块名只能与已验证的函数名一起提供。", nameof(moduleName));
            }

            FunctionName = null;
            ModuleName = null;
        }
        else
        {
            FunctionName = functionName;
            ModuleName = string.IsNullOrWhiteSpace(moduleName) ? null : moduleName;
        }

        Kind = kind;
        Flags = flags;
    }

    /// <summary>
    /// CLR 报告的根类别。
    /// </summary>
    public MemoryRootKind Kind { get; }

    /// <summary>
    /// CLR 报告的根标志。
    /// </summary>
    public MemoryRootFlags Flags { get; }

    /// <summary>
    /// 经 CLR 栈根 FunctionID 验证后的持有函数全名；没有证据时为空。
    /// </summary>
    public string? FunctionName { get; }

    /// <summary>
    /// 已验证函数所属模块名；没有函数证据时为空。
    /// </summary>
    public string? ModuleName { get; }
}
