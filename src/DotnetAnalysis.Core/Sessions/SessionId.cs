namespace DotnetAnalysis.Core.Sessions;

public readonly record struct SessionId(Guid Value)
{
    public static SessionId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}
