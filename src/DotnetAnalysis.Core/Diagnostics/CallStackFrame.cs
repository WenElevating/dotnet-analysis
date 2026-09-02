namespace DotnetAnalysis.Core.Diagnostics;

public sealed record CallStackFrame
{
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

    public string Name { get; }

    public string? ModuleName { get; }

    public int? LineNumber { get; }
}
