namespace DotnetAnalysis.Core.Diagnostics;

public readonly record struct ProcessDiagnosticsSessionId
{
    public ProcessDiagnosticsSessionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Session ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static ProcessDiagnosticsSessionId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}
