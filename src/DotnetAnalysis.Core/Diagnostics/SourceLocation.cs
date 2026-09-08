namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示方法对应的源代码位置。
/// </summary>
public sealed record SourceLocation
{
    /// <summary>
    /// 创建源代码位置。
    /// </summary>
    /// <param name="filePath">源文件路径。</param>
    /// <param name="lineNumber">正数源代码行号。</param>
    /// <param name="columnNumber">可选的正数源代码列号。</param>
    /// <exception cref="ArgumentException">源文件路径为空或仅包含空白字符时引发。</exception>
    /// <exception cref="ArgumentOutOfRangeException">行号或列号不是正数时引发。</exception>
    public SourceLocation(string filePath, int lineNumber, int? columnNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (lineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), lineNumber, "Line number must be positive.");
        }

        if (columnNumber is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columnNumber), columnNumber, "Column number must be positive.");
        }

        FilePath = Path.GetFullPath(filePath);
        LineNumber = lineNumber;
        ColumnNumber = columnNumber;
    }

    /// <summary>
    /// 规范化后的源文件绝对路径。
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// 正数源代码行号。
    /// </summary>
    public int LineNumber { get; }

    /// <summary>
    /// 源代码列号；未知时为 <see langword="null"/>。
    /// </summary>
    public int? ColumnNumber { get; }
}
