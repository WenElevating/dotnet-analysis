namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示分配调用栈中的一个方法或源代码位置。
/// </summary>
public sealed record CallStackFrame
{
    /// <summary>
    /// 创建调用栈帧。
    /// </summary>
    /// <param name="name">方法或符号名称。</param>
    /// <param name="moduleName">可选模块名称。</param>
    /// <param name="lineNumber">可选的正数源代码行号。</param>
    public CallStackFrame(string name, string? moduleName, int? lineNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (lineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), lineNumber, "Line number must be positive.");
        }

        Name = name;
        ModuleName = moduleName;
        LineNumber = lineNumber;
    }

    /// <summary>
    /// 方法或符号名称。
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// 所属模块名称。
    /// </summary>
    public string? ModuleName { get; }

    /// <summary>
    /// 源代码行号；未知时为 <see langword="null"/>。
    /// </summary>
    public int? LineNumber { get; }
}
