namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示不可为空的诊断会话标识。
/// </summary>
public readonly record struct ProcessDiagnosticsSessionId
{
    /// <summary>
    /// 以现有 GUID 创建会话标识。
    /// </summary>
    /// <param name="value">非空 GUID 值。</param>
    public ProcessDiagnosticsSessionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Session ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    /// <summary>
    /// 底层 GUID 值。
    /// </summary>
    public Guid Value { get; }

    /// <summary>
    /// 生成新的诊断会话标识。
    /// </summary>
    public static ProcessDiagnosticsSessionId New() => new(Guid.NewGuid());

    /// <summary>
    /// 以无连字符格式显示标识。
    /// </summary>
    public override string ToString() => Value.ToString("N");
}
