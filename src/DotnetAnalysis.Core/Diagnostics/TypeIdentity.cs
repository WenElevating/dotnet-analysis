namespace DotnetAnalysis.Core.Diagnostics;

public sealed record TypeIdentity
{
    public TypeIdentity(string typeName, string? assemblyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);

        TypeName = typeName;
        AssemblyName = assemblyName;
    }

    public string TypeName { get; }

    public string? AssemblyName { get; }
}
