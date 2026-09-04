namespace DotnetAnalysis.Core.Diagnostics;

/// <summary>
/// 表示托管类型名称及其可选程序集来源。
/// </summary>
public sealed record TypeIdentity
{
    /// <summary>
    /// 创建类型标识。
    /// </summary>
    /// <param name="typeName">非空类型全名或显示名。</param>
    /// <param name="assemblyName">可选程序集名称。</param>
    public TypeIdentity(string typeName, string? assemblyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);

        TypeName = typeName;
        AssemblyName = assemblyName;
    }

    /// <summary>
    /// 类型名称。
    /// </summary>
    public string TypeName { get; }

    /// <summary>
    /// 程序集名称；来源未知时为空。
    /// </summary>
    public string? AssemblyName { get; }
}
