namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示执行采样调用栈中的一个方法帧。
/// </summary>
public sealed record ExecutionFrame
{
    /// <summary>
    /// 创建执行调用栈帧。
    /// </summary>
    /// <param name="methodName">方法或符号名称。</param>
    /// <param name="moduleName">可选模块名称。</param>
    /// <param name="sourceLocation">可选源代码位置。</param>
    /// <exception cref="ArgumentException">方法名称为空或仅包含空白字符时引发。</exception>
    public ExecutionFrame(string methodName, string? moduleName, SourceLocation? sourceLocation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);

        MethodName = methodName;
        ModuleName = moduleName;
        SourceLocation = sourceLocation;
    }

    /// <summary>
    /// 方法或符号名称。
    /// </summary>
    public string MethodName { get; }

    /// <summary>
    /// 所属模块名称；未知时为 <see langword="null"/>。
    /// </summary>
    public string? ModuleName { get; }

    /// <summary>
    /// 源代码位置；未知时为 <see langword="null"/>。
    /// </summary>
    public SourceLocation? SourceLocation { get; }
}
